using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitViewPlacer
{
    [Transaction(TransactionMode.Manual)]
    public partial class GenerateDetailedSitePlanCommand : IExternalCommand
    {
        // Коэффициент перевода миллиметров во внутренние единицы Revit (футы)
        private const double MmToFeet = 1.0 / 304.8;

        // Встроенное семейство основной надписи ("Лист (Форма3).rfa", типоразмер "А3_А")
        private const string CustomTitleBlockFamilyName = "Лист (Форма3)";
        private const string CustomTitleBlockTypeName = "А3_А";
        private const string CustomTitleBlockFileName = "Лист (Форма3).rfa";
        private const string EmbeddedTitleBlockResourceName = "RevitViewPlacer.TitleBlockA3.rfa";

        // Стандартный ряд масштабов Revit (1:N)
        private static readonly int[] StandardScales =
        [
            1, 2, 5, 10, 20, 25, 50, 100, 200, 500, 1000, 2000, 5000
        ];

        // Параметры зон 3D-генерального плана (в миллиметрах)
        private const double BlindAreaOffsetMm = 1500.0;     // Отмостка и тротуар вокруг здания: +1500 мм
        private const double RoadOffsetMm = 6000.0;          // Проезд и дорога: +6000 мм
        private const double SiteBoundaryOffsetMm = 15000.0; // Граница участка / озеленения: +15000 мм
        private const double CurbWidthMm = 180.0;            // Ширина 3D-бордюрного камня

        // Габариты одного парковочного машино-места (2.5 х 5.0 м)
        private const double ParkingSpotWidthMm = 2500.0;
        private const double ParkingSpotLengthMm = 5000.0;

        // Габариты листа формата А3 (альбомная ориентация, ГОСТ 21.1101) в миллиметрах
        private const double SheetWidthMm = 420.0;
        private const double SheetHeightMm = 297.0;
        private const double MarginLeftMm = 20.0;
        private const double MarginRightMm = 5.0;
        private const double MarginTopMm = 5.0;
        private const double MarginBottomFrameMm = 5.0;
        private const double StampHeightMm = 55.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiApp = commandData.Application;
                UIDocument? uiDoc = uiApp.ActiveUIDocument;

                if (uiDoc?.Document == null)
                {
                    message = "Нет активного документа проекта Revit.";
                    TaskDialog.Show("3D Генеральный план ГП-1", message);
                    return Result.Failed;
                }

                Document doc = uiDoc.Document;

                if (doc.IsFamilyDocument)
                {
                    message = "Команда предназначена для работы в документе проекта, а не в редакторе семейств.";
                    TaskDialog.Show("3D Генеральный план ГП-1", message);
                    return Result.Failed;
                }

                // Получаем реквизиты штампа по умолчанию и сразу открываем подробный интерактивный редактор ген-плана:
                // отрисовка границ участка в окошке, асфальтированных зон, выбор текстур каждого слоя и экспликация зон
                GostStampData defaultStamp = GostStampHelper.GetDefaultGostStampData(doc);
                (CurveLoop buildingLoop, BoundingBoxXYZ buildingBox) = ExtractBuildingFootprint(doc);

                SitePlanEditorWindow editor = new SitePlanEditorWindow(buildingLoop, buildingBox, defaultStamp);
                bool? dialogResult = editor.ShowDialog();
                if (dialogResult != true || editor.ResultConfig == null)
                {
                    return Result.Cancelled;
                }
                SitePlanEditorConfig editorConfig = editor.ResultConfig;
                GostStampData gostStamp = editor.UpdatedGostStamp ?? defaultStamp;
                GostStampHelper.SaveLastGostStampData(gostStamp);

                // Убеждаемся, что семейство "Лист (Форма3)" загружено, и вписываем имя в шаблонные строки штампа
                FamilySymbol? tbSymbol;
                using (Transaction txLoadTb = new Transaction(doc, "Загрузка семейства листа Форма 3"))
                {
                    SheetLayoutEngine.ConfigureSilentTransaction(txLoadTb);
                    txLoadTb.Start();
                    tbSymbol = FindOrLoadTitleBlockSymbol(doc);
                    txLoadTb.Commit();
                }

                if (tbSymbol?.Family != null)
                {
                    GostStampHelper.UpdateTitleBlockFamilyStampTexts(doc, tbSymbol.Family, gostStamp);
                }

                DetailedSitePlanSummary summary;

                using (Transaction tx = new Transaction(doc, "Генерация 3D-модели генплана ГП-1 и ТЭП"))
                {
                    SheetLayoutEngine.ConfigureSilentTransaction(tx);
                    tx.Start();

                    summary = ExecuteSitePlanPipeline(doc, gostStamp, editorConfig);

                    tx.Commit();
                }

                TaskDialog.Show(
                    "3D Генеральный план (ГП-1)",
                    $"3D-модель генерального плана успешно создана!\n" +
                    $"Стиль дизайна: {summary.StyleDescription}\n" +
                    $"Разработал (по ГОСТ): {gostStamp.FormattedDeveloperName} (дата: {gostStamp.IssueDateGost})\n\n" +
                    $"1. Созданные 3D-элементы благоустройства:\n" +
                    $"   • Габариты здания: {summary.BuildingWidthM:F2} × {summary.BuildingLengthM:F2} м\n" +
                    $"   • 3D-отмостка, тротуары, площадь и бордюры\n" +
                    $"   • 3D-парковка на {summary.ParkingSpotsCount} м/мест (с 3D-разметкой и упорами)\n" +
                    $"   • 3D-озеленение (газон, {summary.CreatedTreesCount} 3D-деревьев, кустарники, фонари, ограждение)\n\n" +
                    $"2. Баланс территории (ТЭП):\n" +
                    $"   • Площадь застройки: {summary.BuildingAreaM2:F2} м²\n" +
                    $"   • Отмостка, площадь и тротуары: {summary.BlindAreaM2:F2} м²\n" +
                    $"   • Дороги и парковки (асфальт): {summary.RoadsAndParkingAreaM2:F2} м²\n" +
                    $"   • Газон и озеленение: {summary.GreenAreaM2:F2} м²\n" +
                    $"   • Общая площадь участка: {summary.TotalSiteAreaM2:F2} м²\n\n" +
                    $"3. Оформление на лист:\n" +
                    $"   • Лист: \"{summary.SheetNumber} — {summary.SheetName}\" (План М 1:{summary.ViewScale} + 3D-вид + ТЭП)");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Ошибка генерации 3D-генплана", $"Произошла ошибка:\n{ex.Message}");
                return Result.Failed;
            }
        }

        internal sealed record DetailedSitePlanSummary(
            string SheetNumber,
            string SheetName,
            int ViewScale,
            string StyleDescription,
            double BuildingWidthM,
            double BuildingLengthM,
            int CreatedGridsCount,
            int CreatedTreesCount,
            int ParkingSpotsCount,
            double BuildingAreaM2,
            double BlindAreaM2,
            double RoadAreaM2,
            double ParkingAreaM2,
            double RoadsAndParkingAreaM2,
            double GreenAreaM2,
            double TotalSiteAreaM2);

        private sealed record SiteMaterials(
            ElementId GrassMaterialId,
            ElementId AsphaltMaterialId,
            ElementId PavingTileMaterialId,
            ElementId CurbConcreteMaterialId,
            ElementId WhiteStripeMaterialId,
            ElementId YellowStopperMaterialId,
            ElementId TreeTrunkMaterialId,
            ElementId TreeFoliageMaterialId,
            ElementId ShrubFoliageMaterialId,
            ElementId MetalDarkMaterialId,
            ElementId LampGlowMaterialId,
            ElementId WoodBenchMaterialId,
            ElementId PlaygroundYellowMaterialId,
            ElementId SportsCourtOrangeMaterialId,
            ElementId RedBoundaryMaterialId,
            ElementId WasteSiteMaterialId);

        private sealed record SitePlanExplicationData(
            string BuildingPurposeName,
            BuildingPurposeCategory PurposeCategory,
            List<string[]> Table1Rows,
            List<string[]> Table2Rows,
            string Calc1Header,
            string Calc1Formula,
            string Calc1Desc,
            string Calc1Result,
            string Calc2Header,
            string Calc2Formula,
            string Calc2Result,
            double BuildingAreaM2,
            double RoadAreaM2,
            double BlindAreaM2,
            double ParkingAreaM2,
            double GreenAreaM2,
            double TotalSiteAreaM2,
            int ParkingSpotsCount);

        private static double s_currentSiteBaseZ;

        internal static (CurveLoop BuildingLoop, BoundingBoxXYZ BuildingBox) ExtractBuildingFootprint(Document doc)
        {
            Level firstLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation))
                .ThenBy(l => l.Elevation)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("В проекте не найден ни один уровень (Level).");

            s_currentSiteBaseZ = firstLevel.Elevation;

            List<Wall> exteriorWalls = CollectFirstFloorExteriorWalls(doc, firstLevel);
            BoundingBoxXYZ buildingBox = ComputeBuildingBoundingBox(exteriorWalls, firstLevel.Elevation);
            CurveLoop buildingLoop = BuildClosedBuildingFootprint(exteriorWalls, buildingBox, firstLevel.Elevation);
            return (buildingLoop, buildingBox);
        }

        /// <summary>
        /// Выполняет полный цикл анализа 3D-модели здания, генерации полноценной 3D-модель генплана,
        /// расчета ТЭП и оформления листа "ГП-1" (2D-план + 3D-изометрия + таблица ТЭП + штамп по ГОСТ).
        /// </summary>
        internal static DetailedSitePlanSummary ExecuteSitePlanPipeline(
            Document doc,
            GostStampData? gostStamp = null,
            SitePlanEditorConfig? editorConfig = null,
            string? targetSheetNumber = null,
            int? sequentialPageNumber = null,
            int? totalSheetsCount = null)
        {
            // 1. Анализ 3D-модели здания: наружные стены первого этажа и BoundingBoxXYZ
            Level firstLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation))
                .ThenBy(l => l.Elevation)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("В проекте не найден ни один уровень (Level).");

            List<Wall> exteriorWalls = CollectFirstFloorExteriorWalls(doc, firstLevel);
            BoundingBoxXYZ buildingBox = ComputeBuildingBoundingBox(exteriorWalls, firstLevel.Elevation);

            double buildingWidthM = UnitUtils.ConvertFromInternalUnits(
                buildingBox.Max.X - buildingBox.Min.X,
                UnitTypeId.Meters);
            double buildingLengthM = UnitUtils.ConvertFromInternalUnits(
                buildingBox.Max.Y - buildingBox.Min.Y,
                UnitTypeId.Meters);

            double baseZ = firstLevel.Elevation;
            s_currentSiteBaseZ = baseZ;

            // 2. Формирование контура здания и смещенных зон благоустройства (CurveLoop.CreateViaOffset)
            CurveLoop buildingLoop = BuildClosedBuildingFootprint(exteriorWalls, buildingBox, baseZ);
            if (!buildingLoop.IsCounterclockwise(XYZ.BasisZ))
            {
                buildingLoop.Flip();
            }

            CurveLoop curbInnerLoop = CurveLoop.CreateViaOffset(
                buildingLoop,
                (BlindAreaOffsetMm - CurbWidthMm) * MmToFeet,
                XYZ.BasisZ);

            CurveLoop blindAreaLoop = CurveLoop.CreateViaOffset(
                buildingLoop,
                BlindAreaOffsetMm * MmToFeet,
                XYZ.BasisZ);

            CurveLoop roadLoop = CurveLoop.CreateViaOffset(
                buildingLoop,
                RoadOffsetMm * MmToFeet,
                XYZ.BasisZ);

            CurveLoop roadOuterCurbLoop = CurveLoop.CreateViaOffset(
                buildingLoop,
                (RoadOffsetMm + CurbWidthMm) * MmToFeet,
                XYZ.BasisZ);

            // 3. Создаем цветные 3D-материалы со сплошной заливкой для покрытий, озеленения и МАФ (с учетом настроек редактора)
            SiteMaterials materials = CreateOrUpdateSiteMaterials(doc, editorConfig);

            // Удаляем старые элементы ГП при повторном запуске
            CleanupPreviousSiteElements(doc);

            // 4. Определение стиля благоустройства, формы участка, назначения объекта и ориентации входа
            SitePlanStyle requestedStyle = gostStamp?.SelectedSitePlanStyle ?? SitePlanStyle.RandomProcedural;
            SiteBoundaryShape requestedShape = gostStamp?.SelectedBoundaryShape ?? SiteBoundaryShape.CornerLot;
            string buildingPurposeName = string.IsNullOrWhiteSpace(gostStamp?.BuildingPurpose)
                ? "Ресторан"
                : gostStamp.BuildingPurpose.Trim();
            BuildingPurposeCategory purposeCategory = gostStamp?.PurposeCategory
                ?? GostStampHelper.ParseBuildingPurposeCategory(buildingPurposeName);
            Random rng = new Random();

            bool isRandomChoice = requestedStyle == SitePlanStyle.RandomProcedural;
            SitePlanStyle effectiveStyle = requestedStyle;
            if (isRandomChoice)
            {
                SitePlanStyle[] concreteStyles =
                [
                    SitePlanStyle.CornerHub,
                    SitePlanStyle.FrontPlaza,
                    SitePlanStyle.BusinessHub,
                    SitePlanStyle.GreenCampus
                ];
                effectiveStyle = concreteStyles[rng.Next(concreteStyles.Length)];
            }

            SiteBoundaryShape effectiveShape = requestedShape;
            if (effectiveShape == SiteBoundaryShape.Random)
            {
                SiteBoundaryShape[] concreteShapes =
                [
                    SiteBoundaryShape.CornerLot,
                    SiteBoundaryShape.Trapezoidal,
                    SiteBoundaryShape.Polygonal
                ];
                effectiveShape = concreteShapes[rng.Next(concreteShapes.Length)];
            }

            // Формируем контур границы участка (красную линию по ГОСТ 21.508)
            // Если в редакторе пользователь отрисовал свой контур — используем его!
            CurveLoop siteBoundaryLoop;
            if (editorConfig?.BoundaryPoints != null && editorConfig.BoundaryPoints.Count >= 3)
            {
                XYZ bldgCenter = (buildingBox.Min + buildingBox.Max) / 2.0;
                List<XYZ> bPts = editorConfig.BoundaryPoints
                    .Select(p => new XYZ(
                        bldgCenter.X + (p.X / 0.3048),
                        bldgCenter.Y + (p.Y / 0.3048),
                        baseZ))
                    .ToList();
                siteBoundaryLoop = BuildLoopFromPoints(bPts, baseZ);
            }
            else
            {
                siteBoundaryLoop = BuildSiteBoundaryLoop(buildingBox, baseZ, effectiveShape, effectiveStyle, rng);
            }

            // 4.1. Анализ реальных дверей и помещений модели (СП 118.13330 / ГОСТ 21.508)
            BuildingDoorRoomAnalysis analysis = AnalyzeBuildingDoorsAndRooms(doc, firstLevel, buildingBox, exteriorWalls);
            EntranceInfo entrance = analysis.MainDoor != null
                ? new EntranceInfo(analysis.MainDoor.Side, analysis.MainDoor.FacadePoint, HasRealDoor: true)
                : DetectMainEntrance(doc, firstLevel, buildingBox, exteriorWalls, rng);

            // 5. Подготавливаем виды генплана и строим полноценную 3D-модель в выбранном стиле
            BeginPlanMarkers();
            ViewPlan sitePlanView = GetOrCreateSitePlanView(doc, firstLevel, siteBoundaryLoop);
            CleanupSitePlanAnnotations(doc, sitePlanView);

            View3D? site3DView = GetOrCreateSite3DView(doc, siteBoundaryLoop, baseZ);

            int parkingSpotsCount = BuildFull3DSiteModel(
                doc,
                firstLevel,
                sitePlanView,
                buildingBox,
                baseZ,
                buildingLoop,
                curbInnerLoop,
                blindAreaLoop,
                roadLoop,
                roadOuterCurbLoop,
                siteBoundaryLoop,
                materials,
                effectiveStyle,
                entrance,
                purposeCategory,
                buildingPurposeName,
                analysis,
                rng,
                editorConfig,
                out int createdTreesCount,
                out double drivewayAndWalkwayExtraRoadM2,
                out double walkwayExtraTileM2,
                out List<CurveLoop> extraWalkways,
                out List<CurveLoop> extraDriveways,
                out CurveLoop accountingParkingLoop,
                out SitePlanExplicationData explicationData);

            // 5.1. Добавляем пользовательские асфальтированные и тротуарные зоны, отрисованные вручную в редакторе
            XYZ bCenterPts = (buildingBox.Min + buildingBox.Max) / 2.0;
            ElementId siteCatId = new ElementId(BuiltInCategory.OST_Site);

            if (editorConfig?.AsphaltZones != null && editorConfig.AsphaltZones.Count > 0)
            {
                int zIdx = 1;
                foreach (SiteZonePolygon z in editorConfig.AsphaltZones)
                {
                    if (!z.IsDefaultPerimeterRing && z.Points.Count >= 3)
                    {
                        List<XYZ> zPts = z.Points.Select(p => new XYZ(bCenterPts.X + (p.X / 0.3048), bCenterPts.Y + (p.Y / 0.3048), baseZ)).ToList();
                        CurveLoop cLoop = BuildLoopFromPoints(zPts, baseZ);
                        extraDriveways.Add(cLoop);
                        Create3DExtrusionDirectShape(
                            doc,
                            siteCatId,
                            [cLoop],
                            heightFt: 118.0 * MmToFeet,
                            materials.AsphaltMaterialId,
                            $"ГП_3D: Асфальтированная зона #{zIdx++} ({z.Name})",
                            "ГП-ДОР");
                    }
                }
            }

            if (editorConfig?.PavingZones != null && editorConfig.PavingZones.Count > 0)
            {
                int pIdx = 1;
                foreach (SiteZonePolygon z in editorConfig.PavingZones)
                {
                    if (!z.IsDefaultPerimeterRing && z.Points.Count >= 3)
                    {
                        List<XYZ> zPts = z.Points.Select(p => new XYZ(bCenterPts.X + (p.X / 0.3048), bCenterPts.Y + (p.Y / 0.3048), baseZ)).ToList();
                        CurveLoop cLoop = BuildLoopFromPoints(zPts, baseZ);
                        extraWalkways.Add(cLoop);
                        Create3DExtrusionDirectShape(
                            doc,
                            siteCatId,
                            [cLoop],
                            heightFt: 132.0 * MmToFeet,
                            materials.PavingTileMaterialId,
                            $"ГП_3D: Тротуар / мощение #{pIdx++} ({z.Name})",
                            "ГП-ТРОТ");
                    }
                }
            }

            // 5.2. Настраиваем отображение на плане генплана (ПОСЛЕ создания всех зон):
            // скрываем внутренние стены, помещения и двери, накладываем сверху чистый белый силуэт здания с черным контуром по ГОСТ
            ConfigureSitePlanViewDisplay(doc, sitePlanView, buildingLoop, baseZ, materials.WhiteStripeMaterialId);

            string shapeDesc = effectiveShape switch
            {
                SiteBoundaryShape.CornerLot => "угловой участок со скосом улиц (7 вершин)",
                SiteBoundaryShape.Trapezoidal => "трапециевидный участок со скосом (6 вершин)",
                SiteBoundaryShape.Polygonal => "сложный многоугольный участок (8 вершин)",
                _ => "прямоугольный классический (4 вершины)"
            };

            string styleDesc = effectiveStyle switch
            {
                SitePlanStyle.CornerHub => $"📐 Угловой квартал ({shapeDesc}, угловой въезд и сквер по образцу ГОСТ)",
                SitePlanStyle.FrontPlaza => $"🏛️ Представительский ({shapeDesc}, парадная площадь и боковая парковка)",
                SitePlanStyle.BusinessHub => $"🚗 Деловой центр ({shapeDesc}, расширенная парковка на {parkingSpotsCount} мест)",
                SitePlanStyle.GreenCampus => $"🌳 Парковый эко-кампус ({shapeDesc}, прогулочные аллеи и {createdTreesCount} деревьев)",
                _ => $"🎲 Генеративный дизайн ({shapeDesc})"
            };

            if (isRandomChoice)
            {
                styleDesc = "🎲 [Случайный дизайн] " + styleDesc;
            }

            // 6. Расчет площадей в м² и создание спецификации (ViewSchedule) с балансом территории
            double buildingAreaM2 = editorConfig?.BuildingAreaM2 ?? explicationData.BuildingAreaM2;
            double blindAreaM2 = editorConfig?.PavingAreaM2 ?? explicationData.BlindAreaM2;
            double roadAreaM2 = editorConfig?.AsphaltAreaM2 ?? explicationData.RoadAreaM2;
            double parkingAreaM2 = editorConfig != null ? 0.0 : explicationData.ParkingAreaM2;
            double roadsAndParkingAreaM2 = roadAreaM2 + parkingAreaM2;
            double totalSiteAreaM2 = editorConfig?.TotalSiteAreaM2 ?? explicationData.TotalSiteAreaM2;
            double greenAreaM2 = editorConfig?.GreenAreaM2 ?? Math.Max(0.0, totalSiteAreaM2 - buildingAreaM2 - roadsAndParkingAreaM2 - blindAreaM2);
            if (greenAreaM2 <= 0 && explicationData.GreenAreaM2 > 0) greenAreaM2 = explicationData.GreenAreaM2;

            ViewSchedule balanceSchedule = CreateTerritoryBalanceSchedule(
                doc,
                buildingAreaM2,
                blindAreaM2,
                roadsAndParkingAreaM2,
                greenAreaM2,
                totalSiteAreaM2,
                parkingSpotsCount);

            // 7. Оформление на лист А3 "ГП-1" (План по ГОСТ 21.508-2020: экспликация + расчеты площадок + расчет ТБО + роза ветров)
            FamilySymbol titleBlockSymbol = FindOrLoadTitleBlockSymbol(doc)
                ?? throw new InvalidOperationException("В документе не найдено семейство основной надписи (OST_TitleBlocks).");

            if (!titleBlockSymbol.IsActive)
            {
                titleBlockSymbol.Activate();
                doc.Regenerate();
            }

            ApplyColorOverridesToSiteView(doc, sitePlanView, materials, editorConfig);
            if (site3DView != null)
            {
                ApplyColorOverridesToSiteView(doc, site3DView, materials, editorConfig);
            }
            foreach (View3D v3d in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate))
            {
                ApplyColorOverridesToSiteView(doc, v3d, materials, editorConfig);
            }

            string finalSheetNumber = !string.IsNullOrWhiteSpace(targetSheetNumber) ? targetSheetNumber : "ГП-1";

            ViewSheet? existingGpSheet = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .FirstOrDefault(s => !s.IsTemplate &&
                    (s.SheetNumber.Equals(finalSheetNumber, StringComparison.OrdinalIgnoreCase) ||
                     s.SheetNumber.Equals("ГП-1", StringComparison.OrdinalIgnoreCase) ||
                     s.Name.Equals("Схема генерального плана", StringComparison.OrdinalIgnoreCase)));

            ViewSheet gpSheet = existingGpSheet ?? ViewSheet.Create(doc, titleBlockSymbol.Id);
            if (existingGpSheet != null)
            {
                // Очищаем существующий открытый лист от старых видовых экранов, линий и текстов
                ClearSheetElements(doc, gpSheet);
            }

            gpSheet.Name = "Схема генерального плана";
            AssignUniqueSheetNumber(doc, gpSheet, finalSheetNumber);

            ConfigureTitleBlockInstanceA3(doc, gpSheet);
            if (gostStamp != null)
            {
                int pageNum = sequentialPageNumber ?? 1;
                GostStampHelper.ApplyGostStampToSheet(doc, gpSheet, gostStamp, sequentialPageNumber: pageNum, totalSheetsCount: totalSheetsCount);
            }
            doc.Regenerate();

            (double originX, double originY) = GetSheetOrigin(doc, gpSheet);

            // А) Размещаем таблицы экспликации и расчетов по ГОСТ в правом столбце листа (над основной надписью)
            DrawGostSiteExplicationTablesOnSheet(doc, gpSheet, originX, originY, explicationData, editorConfig);

            // Б) Размещаем архитектурную розу ветров и указатель "Север" в левом верхнем углу листа
            DrawArchitecturalWindRoseOnSheet(doc, gpSheet, originX, originY);

            // В) Размещаем 2D-план генерального плана в левой части листа А3 и центрируем по ГОСТ
            double rightColumnWidthMm = 146.0;
            double leftZoneMinXFt = originX + (MarginLeftMm * MmToFeet);
            double leftZoneMaxXFt = originX + ((SheetWidthMm - MarginRightMm - rightColumnWidthMm) * MmToFeet);
            double leftZoneMinYFt = originY + (MarginBottomFrameMm * MmToFeet);
            double leftZoneMaxYFt = originY + ((SheetHeightMm - MarginTopMm) * MmToFeet);

            int? userSitePlanScale = gostStamp is { SitePlanScale: > 0 } ? gostStamp.SitePlanScale : null;
            if (userSitePlanScale is > 0)
            {
                SheetLayoutEngine.TrySetViewScale(sitePlanView, userSitePlanScale.Value);
                doc.Regenerate();
            }

            Viewport? planVp = new FilteredElementCollector(doc, gpSheet.Id)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .FirstOrDefault(vp => vp.ViewId == sitePlanView.Id);

            if (planVp == null && Viewport.CanAddViewToSheet(doc, gpSheet.Id, sitePlanView.Id))
            {
                XYZ initialCenter = new XYZ(
                    (leftZoneMinXFt + leftZoneMaxXFt) / 2.0,
                    (leftZoneMinYFt + leftZoneMaxYFt) / 2.0,
                    0.0);

                planVp = Viewport.Create(doc, gpSheet.Id, sitePlanView.Id, initialCenter);
                doc.Regenerate();
            }

            int selectedScale = sitePlanView.Scale;
            if (planVp != null)
            {
                selectedScale = SheetLayoutEngine.FitAndCenterViewportInZone(
                    doc,
                    sitePlanView,
                    planVp,
                    leftZoneMinXFt,
                    leftZoneMinYFt,
                    leftZoneMaxXFt,
                    leftZoneMaxYFt,
                    userForcedScale: userSitePlanScale);

                SheetLayoutEngine.CreateOrUpdateGostViewTitleNote(doc, gpSheet, planVp, sitePlanView, selectedScale);
            }

            // Отрисовка отложенных марок (кружок Ø8 мм + центрированный номер) в точном масштабе листа
            DrawPendingPlanMarkers(doc, sitePlanView, selectedScale);

            return new DetailedSitePlanSummary(
                gpSheet.SheetNumber,
                gpSheet.Name,
                selectedScale,
                styleDesc,
                buildingWidthM,
                buildingLengthM,
                CreatedGridsCount: 0,
                createdTreesCount,
                parkingSpotsCount,
                buildingAreaM2,
                blindAreaM2,
                roadAreaM2,
                parkingAreaM2,
                roadsAndParkingAreaM2,
                greenAreaM2,
                totalSiteAreaM2);
        }

        private enum BuildingSide
        {
            South,
            North,
            West,
            East
        }

        private sealed record EntranceInfo(BuildingSide Side, XYZ EntrancePoint, bool HasRealDoor);

        /// <summary>
        /// Анализирует наружные стены и двери (OST_Doors) первого этажа для выявления главного фасада
        /// и точной координаты парадного входа здания, к которому привязываются генеративные площади и дорожки.
        /// </summary>
        private static EntranceInfo DetectMainEntrance(
            Document doc,
            Level firstLevel,
            BoundingBoxXYZ buildingBox,
            List<Wall> exteriorWalls,
            Random rng)
        {
            double bMinX = buildingBox.Min.X;
            double bMaxX = buildingBox.Max.X;
            double bMinY = buildingBox.Min.Y;
            double bMaxY = buildingBox.Max.Y;
            double bCenterX = (bMinX + bMaxX) / 2.0;
            double bCenterY = (bMinY + bMaxY) / 2.0;
            double baseZ = firstLevel.Elevation;

            List<FamilyInstance> doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(d =>
                {
                    if (d.LevelId == firstLevel.Id) return true;
                    BoundingBoxXYZ? b = d.get_BoundingBox(null);
                    return b != null && Math.Abs(b.Min.Z - baseZ) < 3000.0 * MmToFeet;
                })
                .ToList();

            List<(XYZ Point, BuildingSide Side, double Dist)> doorCandidates = [];

            foreach (FamilyInstance door in doors)
            {
                XYZ pt = (door.Location as LocationPoint)?.Point
                         ?? door.get_BoundingBox(null)?.Min
                         ?? XYZ.Zero;

                double dSouth = Math.Abs(pt.Y - bMinY);
                double dNorth = Math.Abs(pt.Y - bMaxY);
                double dWest = Math.Abs(pt.X - bMinX);
                double dEast = Math.Abs(pt.X - bMaxX);

                double minDist = Math.Min(Math.Min(dSouth, dNorth), Math.Min(dWest, dEast));
                if (minDist <= 2500.0 * MmToFeet)
                {
                    BuildingSide side = BuildingSide.South;
                    if (Math.Abs(minDist - dSouth) < 1e-4) side = BuildingSide.South;
                    else if (Math.Abs(minDist - dNorth) < 1e-4) side = BuildingSide.North;
                    else if (Math.Abs(minDist - dWest) < 1e-4) side = BuildingSide.West;
                    else if (Math.Abs(minDist - dEast) < 1e-4) side = BuildingSide.East;

                    doorCandidates.Add((pt, side, minDist));
                }
            }

            if (doorCandidates.Count > 0)
            {
                var bestSideGroup = doorCandidates
                    .GroupBy(c => c.Side)
                    .OrderByDescending(g => g.Count())
                    .First();

                BuildingSide bestSide = bestSideGroup.Key;
                XYZ bestPoint = bestSideGroup
                    .OrderBy(c => bestSide is BuildingSide.South or BuildingSide.North
                        ? Math.Abs(c.Point.X - bCenterX)
                        : Math.Abs(c.Point.Y - bCenterY))
                    .First().Point;

                return new EntranceInfo(bestSide, bestPoint, HasRealDoor: true);
            }

            return new EntranceInfo(BuildingSide.South, new XYZ(bCenterX, bMinY, baseZ), HasRealDoor: false);
        }

        // =========================================================================
        // ПОСТРОЕНИЕ ПОЛНОЦЕННОЙ 3D-МОДЕЛИ ГЕНЕРАЛЬНОГО ПЛАНА И БЛАГОУСТРОЙСТВА
        // =========================================================================

        private static int BuildFull3DSiteModel(
            Document doc,
            Level level,
            ViewPlan sitePlanView,
            BoundingBoxXYZ buildingBox,
            double baseZ,
            CurveLoop buildingLoop,
            CurveLoop curbInnerLoop,
            CurveLoop blindAreaLoop,
            CurveLoop roadLoop,
            CurveLoop roadOuterCurbLoop,
            CurveLoop siteBoundaryLoop,
            SiteMaterials materials,
            SitePlanStyle effectiveStyle,
            EntranceInfo entrance,
            BuildingPurposeCategory purposeCategory,
            string buildingPurposeName,
            BuildingDoorRoomAnalysis analysis,
            Random rng,
            SitePlanEditorConfig? editorConfig,
            out int createdTreesCount,
            out double drivewayExtraRoadM2,
            out double walkwayExtraTileM2,
            out List<CurveLoop> extraWalkways,
            out List<CurveLoop> extraDriveways,
            out CurveLoop accountingParkingLoop,
            out SitePlanExplicationData explicationData)
        {
            extraWalkways = [];
            extraDriveways = [];
            drivewayExtraRoadM2 = 0.0;
            walkwayExtraTileM2 = 0.0;

            if (purposeCategory == BuildingPurposeCategory.Restaurant)
            {
                explicationData = BuildRestaurantSiteByAnalysis(
                    doc,
                    buildingBox,
                    baseZ,
                    buildingLoop,
                    blindAreaLoop,
                    roadLoop,
                    siteBoundaryLoop,
                    materials,
                    analysis,
                    buildingPurposeName,
                    extraWalkways,
                    extraDriveways,
                    editorConfig,
                    out int spots,
                    out createdTreesCount,
                    out drivewayExtraRoadM2,
                    out walkwayExtraTileM2,
                    out accountingParkingLoop);
                return spots;
            }

            List<XYZ> bPts = GetLoopVertices(buildingLoop);
            List<XYZ> rPts = GetLoopVertices(roadLoop);
            List<XYZ> sPts = GetLoopVertices(siteBoundaryLoop);

            double rMinX = rPts.Min(p => p.X);
            double rMaxX = rPts.Max(p => p.X);
            double rMinY = rPts.Min(p => p.Y);
            double rMaxY = rPts.Max(p => p.Y);

            double sMinX = sPts.Min(p => p.X);
            double sMaxX = sPts.Max(p => p.X);
            double sMinY = sPts.Min(p => p.Y);
            double sMaxY = sPts.Max(p => p.Y);

            ElementId siteCatId = new ElementId(BuiltInCategory.OST_Site);

            // 1. 3D-основание участка (газон / озеленение толщиной 250 мм, верх на -100 мм)
            Create3DExtrusionDirectShape(
                doc,
                siteCatId,
                [ShiftLoopZ(siteBoundaryLoop, baseZ - (350.0 * MmToFeet))],
                heightFt: 250.0 * MmToFeet,
                materials.GrassMaterialId,
                "ГП_3D: Газон и озеленение территории (+15000 мм)",
                "ГП-ГАЗ");

            // 2. 3D-дорожное полотно (кольцевой проезд вокруг здания, верх на -250 мм, толщина 200 мм)
            CurveLoop roadOutZ = ShiftLoopZ(roadLoop, baseZ - (450.0 * MmToFeet));
            CurveLoop roadInZ = ShiftLoopZ(blindAreaLoop, baseZ - (450.0 * MmToFeet));
            roadInZ.Flip();

            Create3DExtrusionDirectShape(
                doc,
                siteCatId,
                [roadOutZ, roadInZ],
                heightFt: 200.0 * MmToFeet,
                materials.AsphaltMaterialId,
                "ГП_3D: Кольцевой проезд и автодорога — асфальт (+6000 мм)",
                "ГП-ДОР");

            // 3. 3D-отмостка и тротуар вокруг здания (плитка, верх на -100 мм, толщина 200 мм)
            CurveLoop blindOutZ = ShiftLoopZ(curbInnerLoop, baseZ - (300.0 * MmToFeet));
            CurveLoop blindInZ = ShiftLoopZ(buildingLoop, baseZ - (300.0 * MmToFeet));
            blindInZ.Flip();

            Create3DExtrusionDirectShape(
                doc,
                siteCatId,
                [blindOutZ, blindInZ],
                heightFt: 200.0 * MmToFeet,
                materials.PavingTileMaterialId,
                "ГП_3D: Отмостка и тротуары — тротуарная плитка (+1500 мм)",
                "ГП-ТРОТ");

            // 4. 3D-бордюрный камень (внутренний тротуара и внешний дороги, верх на -100 мм, высота 300 мм)
            CurveLoop innerCurbOut = ShiftLoopZ(blindAreaLoop, baseZ - (400.0 * MmToFeet));
            CurveLoop innerCurbIn = ShiftLoopZ(curbInnerLoop, baseZ - (400.0 * MmToFeet));
            innerCurbIn.Flip();

            Create3DExtrusionDirectShape(
                doc,
                siteCatId,
                [innerCurbOut, innerCurbIn],
                heightFt: 300.0 * MmToFeet,
                materials.CurbConcreteMaterialId,
                "ГП_3D: Бордюрный камень БР 100.30.15 (внутренний)",
                "ГП-БОРД-1");

            CurveLoop outerCurbOut = ShiftLoopZ(roadOuterCurbLoop, baseZ - (400.0 * MmToFeet));
            CurveLoop outerCurbIn = ShiftLoopZ(roadLoop, baseZ - (400.0 * MmToFeet));
            outerCurbIn.Flip();

            Create3DExtrusionDirectShape(
                doc,
                siteCatId,
                [outerCurbOut, outerCurbIn],
                heightFt: 300.0 * MmToFeet,
                materials.CurbConcreteMaterialId,
                "ГП_3D: Бордюрный камень БР 100.30.15 (внешний)",
                "ГП-БОРД-2");

            // 5. Ветвление генерации по выбранному процедурному стилю благоустройства
            int parkingSpotsCount = effectiveStyle switch
            {
                SitePlanStyle.FrontPlaza => BuildFrontPlazaStyle(
                    doc, level, buildingBox, baseZ,
                    rMinX, rMaxX, rMinY, rMaxY,
                    sMinX, sMaxX, sMinY, sMaxY,
                    materials, entrance, rng,
                    out createdTreesCount,
                    out drivewayExtraRoadM2,
                    out walkwayExtraTileM2,
                    extraWalkways,
                    extraDriveways,
                    out accountingParkingLoop),

                SitePlanStyle.BusinessHub => BuildBusinessHubStyle(
                    doc, level, buildingBox, baseZ,
                    rMinX, rMaxX, rMinY, rMaxY,
                    sMinX, sMaxX, sMinY, sMaxY,
                    materials, entrance, rng,
                    out createdTreesCount,
                    out drivewayExtraRoadM2,
                    out walkwayExtraTileM2,
                    extraWalkways,
                    extraDriveways,
                    out accountingParkingLoop),

                SitePlanStyle.GreenCampus => BuildGreenCampusStyle(
                    doc, level, buildingBox, baseZ,
                    rMinX, rMaxX, rMinY, rMaxY,
                    sMinX, sMaxX, sMinY, sMaxY,
                    materials, entrance, rng,
                    out createdTreesCount,
                    out drivewayExtraRoadM2,
                    out walkwayExtraTileM2,
                    extraWalkways,
                    extraDriveways,
                    out accountingParkingLoop),

                SitePlanStyle.CornerHub => BuildCornerHubStyle(
                    doc, level, buildingBox, baseZ,
                    rMinX, rMaxX, rMinY, rMaxY,
                    sMinX, sMaxX, sMinY, sMaxY,
                    materials, entrance, rng,
                    out createdTreesCount,
                    out drivewayExtraRoadM2,
                    out walkwayExtraTileM2,
                    extraWalkways,
                    extraDriveways,
                    out accountingParkingLoop),

                _ => BuildBusinessHubStyle(
                    doc, level, buildingBox, baseZ,
                    rMinX, rMaxX, rMinY, rMaxY,
                    sMinX, sMaxX, sMinY, sMaxY,
                    materials, entrance, rng,
                    out createdTreesCount,
                    out drivewayExtraRoadM2,
                    out walkwayExtraTileM2,
                    extraWalkways,
                    extraDriveways,
                    out accountingParkingLoop)
            };

            double buildingAreaM2 = CalculateCurveLoopAreaM2(buildingLoop);
            double blindAreaM2 = Math.Max(0.0, CalculateCurveLoopAreaM2(blindAreaLoop) - buildingAreaM2) + walkwayExtraTileM2;
            double roadAreaM2 = Math.Max(0.0, CalculateCurveLoopAreaM2(roadLoop) - CalculateCurveLoopAreaM2(blindAreaLoop)) + drivewayExtraRoadM2;
            double singleSpotAreaM2 = (ParkingSpotWidthMm / 1000.0) * (ParkingSpotLengthMm / 1000.0);
            double parkingAreaM2 = parkingSpotsCount * singleSpotAreaM2;

            explicationData = CreateGostFunctionalZonesAndMarkers(
                doc,
                sitePlanView,
                siteCatId,
                new ElementId(BuiltInCategory.OST_Planting),
                buildingBox,
                baseZ,
                buildingLoop,
                siteBoundaryLoop,
                rMinX, rMaxX, rMinY, rMaxY,
                sMinX, sMaxX, sMinY, sMaxY,
                buildingAreaM2,
                blindAreaM2,
                roadAreaM2,
                parkingSpotsCount,
                parkingAreaM2,
                accountingParkingLoop,
                effectiveStyle,
                purposeCategory,
                buildingPurposeName,
                materials,
                rng,
                editorConfig);

            return parkingSpotsCount;
        }

        private static int BuildFrontPlazaStyle(
            Document doc,
            Level level,
            BoundingBoxXYZ buildingBox,
            double baseZ,
            double rMinX, double rMaxX, double rMinY, double rMaxY,
            double sMinX, double sMaxX, double sMinY, double sMaxY,
            SiteMaterials materials,
            EntranceInfo entrance,
            Random rng,
            out int createdTreesCount,
            out double drivewayExtraRoadM2,
            out double walkwayExtraTileM2,
            List<CurveLoop> extraWalkways,
            List<CurveLoop> extraDriveways,
            out CurveLoop accountingParkingLoop)
        {
            ElementId siteCatId = new ElementId(BuiltInCategory.OST_Site);
            ElementId plantingCatId = new ElementId(BuiltInCategory.OST_Planting);

            double bMinX = buildingBox.Min.X;
            double bMaxX = buildingBox.Max.X;
            double bMinY = buildingBox.Min.Y;
            double bCenterX = (bMinX + bMaxX) / 2.0;
            double bWidth = bMaxX - bMinX;

            // А. Боковой въезд с улицы на западном фланге
            double drvW = 5500.0 * MmToFeet;
            double drvMinX = sMinX + (1200.0 * MmToFeet);
            double drvMaxX = drvMinX + drvW;
            double drvMinY = sMinY;
            double drvMaxY = rMinY + (100.0 * MmToFeet);

            CurveLoop drvLoop = CreateRectangleLoop(drvMinX, drvMinY, drvMaxX, drvMaxY, baseZ - (450.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [drvLoop], 200.0 * MmToFeet, materials.AsphaltMaterialId,
                "ГП_3D: Боковой въезд на участок — асфальт", "ГП-ВЪЕЗД");

            drivewayExtraRoadM2 = CalculateCurveLoopAreaM2(drvLoop);
            extraDriveways.Add(drvLoop);

            // Б. Парадная площадь перед главным фасадом и пешеходный бульвар ко входу
            double entranceX = entrance.HasRealDoor
                ? Math.Clamp(entrance.EntrancePoint.X, bMinX + (1500.0 * MmToFeet), bMaxX - (1500.0 * MmToFeet))
                : bCenterX;

            double plazaW = Math.Clamp(bWidth * 0.95, 11000.0 * MmToFeet, (sMaxX - drvMaxX) - (4000.0 * MmToFeet));
            double plazaMinX = Math.Max(drvMaxX + (1500.0 * MmToFeet), entranceX - (plazaW / 2.0));
            double plazaMaxX = Math.Min(sMaxX - (1500.0 * MmToFeet), plazaMinX + plazaW);
            double blindSouthY = bMinY - (BlindAreaOffsetMm * MmToFeet);

            CurveLoop plazaLoop = CreateRectangleLoop(plazaMinX, rMinY, plazaMaxX, blindSouthY, baseZ - (300.0 * MmToFeet));
            CurveLoop aveLoop = CreateRectangleLoop(entranceX - (2000.0 * MmToFeet), sMinY, entranceX + (2000.0 * MmToFeet), rMinY, baseZ - (300.0 * MmToFeet));

            Create3DExtrusionDirectShape(doc, siteCatId, [plazaLoop], 200.0 * MmToFeet, materials.PavingTileMaterialId,
                "ГП_3D: Парадная площадь перед фасадом — тротуарная плитка", "ГП-ТРОТ");
            Create3DExtrusionDirectShape(doc, siteCatId, [aveLoop], 200.0 * MmToFeet, materials.PavingTileMaterialId,
                "ГП_3D: Парадный пешеходный бульвар ко входу — тротуарная плитка", "ГП-АЛЛЕЯ");

            walkwayExtraTileM2 = CalculateCurveLoopAreaM2(plazaLoop) + CalculateCurveLoopAreaM2(aveLoop);
            extraWalkways.Add(plazaLoop);
            extraWalkways.Add(aveLoop);

            // Декоративные симметричные клумбы-островки на площади
            List<GeometryObject> flowerbedSolids = [];
            double fbMargin = 800.0 * MmToFeet;
            double fbY0 = rMinY + fbMargin;
            double fbY1 = blindSouthY - fbMargin;
            double fbW = Math.Max(1600.0 * MmToFeet, (plazaMaxX - plazaMinX - (4500.0 * MmToFeet)) / 2.0);

            if (fbY1 - fbY0 > 1500.0 * MmToFeet)
            {
                // Левая клумба
                CurveLoop fbL = CreateRectangleLoop(plazaMinX + fbMargin, fbY0, plazaMinX + fbMargin + fbW, fbY1, baseZ - (100.0 * MmToFeet));
                flowerbedSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    [fbL], XYZ.BasisZ, 450.0 * MmToFeet,
                    new SolidOptions(materials.ShrubFoliageMaterialId, ElementId.InvalidElementId)));

                // Правая клумба
                CurveLoop fbR = CreateRectangleLoop(plazaMaxX - fbMargin - fbW, fbY0, plazaMaxX - fbMargin, fbY1, baseZ - (100.0 * MmToFeet));
                flowerbedSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    [fbR], XYZ.BasisZ, 450.0 * MmToFeet,
                    new SolidOptions(materials.ShrubFoliageMaterialId, ElementId.InvalidElementId)));
            }

            if (flowerbedSolids.Count > 0)
            {
                DirectShape fbShape = DirectShape.CreateElement(doc, siteCatId);
                fbShape.SetShape(flowerbedSolids);
                SetZoneMetadata(fbShape, "ГП_3D: Декоративные клумбы парадной площади", "ГП-КЛУМБА");
            }

            // В. Боковой парковочный карман на 4-7 машино-мест (смещен на фланг)
            double spotW = ParkingSpotWidthMm * MmToFeet;
            double spotL = ParkingSpotLengthMm * MmToFeet;
            double parkStartX = drvMaxX + (800.0 * MmToFeet);
            double availParkW = Math.Max(spotW * 4, (plazaMinX - (600.0 * MmToFeet)) - parkStartX);
            int spotCount = Math.Clamp((int)Math.Floor(availParkW / spotW), 4, 7);
            double totalParkW = spotCount * spotW;

            double parkTopY = rMinY + (50.0 * MmToFeet);
            double parkBottomY = rMinY - spotL;

            accountingParkingLoop = CreateRectangleLoop(parkStartX, parkBottomY, parkStartX + totalParkW, parkTopY, baseZ - (450.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [accountingParkingLoop], 200.0 * MmToFeet, materials.AsphaltMaterialId,
                "ГП_3D: Боковой парковочный карман — асфальт", "ГП-ПАРК");

            // Бордюр парковки
            double curbW = CurbWidthMm * MmToFeet;
            CurveLoop pCurbOut = CreateRectangleLoop(parkStartX - curbW, parkBottomY - curbW, parkStartX + totalParkW + curbW, rMinY, baseZ - (400.0 * MmToFeet));
            CurveLoop pCurbIn = CreateRectangleLoop(parkStartX, parkBottomY, parkStartX + totalParkW, rMinY, baseZ - (400.0 * MmToFeet));
            pCurbIn.Flip();
            Create3DExtrusionDirectShape(doc, siteCatId, [pCurbOut, pCurbIn], 300.0 * MmToFeet, materials.CurbConcreteMaterialId,
                "ГП_3D: Бордюр парковки", "ГП-БОРД");

            CreateParkingMarkingsAndStops(doc, siteCatId, parkStartX, parkBottomY, rMinY, spotCount, spotW, baseZ - (250.0 * MmToFeet), materials);

            // Г. Уличное освещение и представительские скамейки
            List<GeometryObject> mafSolids = [];
            double groundZ = baseZ - (100.0 * MmToFeet);
            mafSolids.AddRange(CreateLampSolid(new XYZ(plazaMinX - (400.0 * MmToFeet), rMinY - (400.0 * MmToFeet), groundZ), 1.0, 1.0, materials));
            mafSolids.AddRange(CreateLampSolid(new XYZ(plazaMaxX + (400.0 * MmToFeet), rMinY - (400.0 * MmToFeet), groundZ), -1.0, 1.0, materials));
            mafSolids.AddRange(CreateLampSolid(new XYZ(plazaMinX - (400.0 * MmToFeet), blindSouthY + (400.0 * MmToFeet), groundZ), 1.0, -1.0, materials));
            mafSolids.AddRange(CreateLampSolid(new XYZ(plazaMaxX + (400.0 * MmToFeet), blindSouthY + (400.0 * MmToFeet), groundZ), -1.0, -1.0, materials));

            double benchCenterY = (rMinY + blindSouthY) / 2.0;
            mafSolids.AddRange(CreateBenchSolid(new XYZ(plazaMinX + fbMargin + fbW + (600.0 * MmToFeet), benchCenterY, groundZ), Math.PI / 2.0, materials));
            mafSolids.AddRange(CreateBenchSolid(new XYZ(plazaMaxX - fbMargin - fbW - (600.0 * MmToFeet), benchCenterY, groundZ), -Math.PI / 2.0, materials));

            if (mafSolids.Count > 0)
            {
                DirectShape mafShape = DirectShape.CreateElement(doc, siteCatId);
                mafShape.SetShape(mafSolids);
                SetZoneMetadata(mafShape, "ГП_3D: Фонари освещения и скамейки парадной площади", "ГП-МАФ");
            }

            // Д. Бульварные деревья
            List<XYZ> treePositions = [];
            double northRowY = (rMaxY + sMaxY) / 2.0;
            double eastColX = (rMaxX + sMaxX) / 2.0;
            double westColX = (sMinX + rMinX) / 2.0;

            for (double x = sMinX + (4000.0 * MmToFeet); x <= sMaxX - (4000.0 * MmToFeet); x += 7500.0 * MmToFeet)
            {
                treePositions.Add(new XYZ(x, northRowY, groundZ));
            }
            for (double y = rMinY; y <= rMaxY; y += 7500.0 * MmToFeet)
            {
                treePositions.Add(new XYZ(eastColX, y, groundZ));
            }
            treePositions.Add(new XYZ(westColX, (rMinY + rMaxY) / 2.0, groundZ));
            treePositions.Add(new XYZ(entranceX - (3500.0 * MmToFeet), sMinY + (2800.0 * MmToFeet), groundZ));
            treePositions.Add(new XYZ(entranceX + (3500.0 * MmToFeet), sMinY + (2800.0 * MmToFeet), groundZ));

            List<GeometryObject> treeSolids = [];
            foreach (XYZ pos in treePositions)
            {
                treeSolids.AddRange(CreateTreeSolid(pos, materials));
            }
            if (treeSolids.Count > 0)
            {
                DirectShape tShape = DirectShape.CreateElement(doc, plantingCatId);
                tShape.SetShape(treeSolids);
                SetZoneMetadata(tShape, "ГП_3D: Бульварные деревья парадной площади", "ГП-ДЕРЕВЬЯ");
            }
            createdTreesCount = treePositions.Count;

            // Е. Ограждение с въездными и парадными воротами
            List<(double Start, double End, BuildingSide Side)> gateOpenings =
            [
                (drvMinX - (300.0 * MmToFeet), drvMaxX + (300.0 * MmToFeet), BuildingSide.South),
                (entranceX - (2500.0 * MmToFeet), entranceX + (2500.0 * MmToFeet), BuildingSide.South)
            ];
            Create3DSitePerimeterFenceWithOpenings(doc, siteCatId, groundZ, sMinX, sMaxX, sMinY, sMaxY, gateOpenings, materials);

            return spotCount;
        }

        private static int BuildBusinessHubStyle(
            Document doc,
            Level level,
            BoundingBoxXYZ buildingBox,
            double baseZ,
            double rMinX, double rMaxX, double rMinY, double rMaxY,
            double sMinX, double sMaxX, double sMinY, double sMaxY,
            SiteMaterials materials,
            EntranceInfo entrance,
            Random rng,
            out int createdTreesCount,
            out double drivewayExtraRoadM2,
            out double walkwayExtraTileM2,
            List<CurveLoop> extraWalkways,
            List<CurveLoop> extraDriveways,
            out CurveLoop accountingParkingLoop)
        {
            ElementId siteCatId = new ElementId(BuiltInCategory.OST_Site);
            ElementId plantingCatId = new ElementId(BuiltInCategory.OST_Planting);

            double bMinX = buildingBox.Min.X;
            double bMaxX = buildingBox.Max.X;
            double bMinY = buildingBox.Min.Y;
            double bCenterX = (bMinX + bMaxX) / 2.0;

            // А. Двухполосный въезд (6.5 м)
            double drvW = 6500.0 * MmToFeet;
            double drvMinX = sMinX + (1200.0 * MmToFeet);
            double drvMaxX = drvMinX + drvW;
            double drvMinY = sMinY;
            double drvMaxY = rMinY + (100.0 * MmToFeet);

            CurveLoop drvLoop = CreateRectangleLoop(drvMinX, drvMinY, drvMaxX, drvMaxY, baseZ - (450.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [drvLoop], 200.0 * MmToFeet, materials.AsphaltMaterialId,
                "ГП_3D: Двухполосный въезд в бизнес-центр — асфальт", "ГП-ВЪЕЗД");

            drivewayExtraRoadM2 = CalculateCurveLoopAreaM2(drvLoop);
            extraDriveways.Add(drvLoop);

            // Б. Расширенная автостоянка на 8-14 машино-мест
            double spotW = ParkingSpotWidthMm * MmToFeet;
            double spotL = ParkingSpotLengthMm * MmToFeet;
            double parkStartX = drvMaxX + (1200.0 * MmToFeet);
            double availParkW = (sMaxX - (1200.0 * MmToFeet)) - parkStartX;
            int spotCount = Math.Clamp((int)Math.Floor(availParkW / spotW), 8, 14);
            double totalParkW = spotCount * spotW;

            double parkTopY = rMinY + (50.0 * MmToFeet);
            double parkBottomY = rMinY - spotL;

            accountingParkingLoop = CreateRectangleLoop(parkStartX, parkBottomY, parkStartX + totalParkW, parkTopY, baseZ - (450.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [accountingParkingLoop], 200.0 * MmToFeet, materials.AsphaltMaterialId,
                "ГП_3D: Расширенная автостоянка бизнес-хаба — асфальт", "ГП-ПАРК");

            double curbW = CurbWidthMm * MmToFeet;
            CurveLoop pCurbOut = CreateRectangleLoop(parkStartX - curbW, parkBottomY - curbW, parkStartX + totalParkW + curbW, rMinY, baseZ - (400.0 * MmToFeet));
            CurveLoop pCurbIn = CreateRectangleLoop(parkStartX, parkBottomY, parkStartX + totalParkW, rMinY, baseZ - (400.0 * MmToFeet));
            pCurbIn.Flip();
            Create3DExtrusionDirectShape(doc, siteCatId, [pCurbOut, pCurbIn], 300.0 * MmToFeet, materials.CurbConcreteMaterialId,
                "ГП_3D: Бордюр автостоянки", "ГП-БОРД");

            // Центральный островок безопасности
            int halfSpots = spotCount / 2;
            double islandCenterX = parkStartX + (halfSpots * spotW);
            double islandW = 1500.0 * MmToFeet;
            CurveLoop safetyIslandLoop = CreateRectangleLoop(
                islandCenterX - (islandW / 2.0),
                parkBottomY + (400.0 * MmToFeet),
                islandCenterX + (islandW / 2.0),
                rMinY - (200.0 * MmToFeet),
                baseZ - (300.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [safetyIslandLoop], 200.0 * MmToFeet, materials.PavingTileMaterialId,
                "ГП_3D: Островок безопасности парковки — плитка", "ГП-ТРОТ");
            walkwayExtraTileM2 = CalculateCurveLoopAreaM2(safetyIslandLoop);
            extraWalkways.Add(safetyIslandLoop);

            CreateParkingMarkingsAndStops(doc, siteCatId, parkStartX, parkBottomY, rMinY, spotCount, spotW, baseZ - (250.0 * MmToFeet), materials);

            // В. Два пешеходных перехода (зебра) через проезд
            List<GeometryObject> zebraSolids = [];
            double blindSouthY = bMinY - (BlindAreaOffsetMm * MmToFeet);
            double entranceX = entrance.HasRealDoor ? entrance.EntrancePoint.X : bCenterX;
            CreateZebraCrossing(zebraSolids, entranceX, rMinY + (300.0 * MmToFeet), blindSouthY - (200.0 * MmToFeet), 2400.0 * MmToFeet, baseZ - (248.0 * MmToFeet), materials);
            CreateZebraCrossing(zebraSolids, drvMaxX - (1500.0 * MmToFeet), rMinY + (300.0 * MmToFeet), blindSouthY - (200.0 * MmToFeet), 2000.0 * MmToFeet, baseZ - (248.0 * MmToFeet), materials);

            if (zebraSolids.Count > 0)
            {
                DirectShape zShape = DirectShape.CreateElement(doc, siteCatId);
                zShape.SetShape(zebraSolids);
                SetZoneMetadata(zShape, "ГП_3D: Пешеходные переходы (зебра) через проезд", "ГП-РАЗМ");
            }

            // Г. Освещение территории делового хаба
            double groundZ = baseZ - (100.0 * MmToFeet);
            List<GeometryObject> lampSolids = [];
            double lampY0 = parkBottomY - (500.0 * MmToFeet);
            lampSolids.AddRange(CreateLampSolid(new XYZ(drvMinX - (400.0 * MmToFeet), lampY0, groundZ), 1.0, 1.0, materials));
            lampSolids.AddRange(CreateLampSolid(new XYZ(islandCenterX, lampY0, groundZ), 0.0, 1.0, materials));
            lampSolids.AddRange(CreateLampSolid(new XYZ(parkStartX + totalParkW + (500.0 * MmToFeet), lampY0, groundZ), -1.0, 1.0, materials));
            lampSolids.AddRange(CreateLampSolid(new XYZ(rMinX - (500.0 * MmToFeet), rMaxY + (500.0 * MmToFeet), groundZ), 1.0, -1.0, materials));
            lampSolids.AddRange(CreateLampSolid(new XYZ(rMaxX + (500.0 * MmToFeet), rMaxY + (500.0 * MmToFeet), groundZ), -1.0, -1.0, materials));
            lampSolids.AddRange(CreateLampSolid(new XYZ(bCenterX, rMaxY + (800.0 * MmToFeet), groundZ), 0.0, -1.0, materials));

            DirectShape lampShape = DirectShape.CreateElement(doc, siteCatId);
            lampShape.SetShape(lampSolids);
            SetZoneMetadata(lampShape, "ГП_3D: Освещение территории делового хаба", "ГП-МАФ");

            // Д. Теневые деревья автостоянки и северного сквера
            List<XYZ> treePositions = [];
            double treeY = sMinY + (2200.0 * MmToFeet);
            for (double tx = parkStartX; tx <= parkStartX + totalParkW; tx += 6500.0 * MmToFeet)
            {
                treePositions.Add(new XYZ(tx, treeY, groundZ));
            }
            double northY = (rMaxY + sMaxY) / 2.0;
            for (double tx = sMinX + (3500.0 * MmToFeet); tx <= sMaxX - (3500.0 * MmToFeet); tx += 7500.0 * MmToFeet)
            {
                treePositions.Add(new XYZ(tx, northY, groundZ));
            }

            List<GeometryObject> treeSolids = [];
            foreach (XYZ pos in treePositions)
            {
                treeSolids.AddRange(CreateTreeSolid(pos, materials));
            }
            DirectShape tShape = DirectShape.CreateElement(doc, plantingCatId);
            tShape.SetShape(treeSolids);
            SetZoneMetadata(tShape, "ГП_3D: Теневые деревья автостоянки и северного сквера", "ГП-ДЕРЕВЬЯ");
            createdTreesCount = treePositions.Count;

            // Е. Ограждение с въездным проемом
            List<(double Start, double End, BuildingSide Side)> gateOpenings =
            [
                (drvMinX - (400.0 * MmToFeet), drvMaxX + (400.0 * MmToFeet), BuildingSide.South)
            ];
            Create3DSitePerimeterFenceWithOpenings(doc, siteCatId, groundZ, sMinX, sMaxX, sMinY, sMaxY, gateOpenings, materials);

            return spotCount;
        }

        private static int BuildGreenCampusStyle(
            Document doc,
            Level level,
            BoundingBoxXYZ buildingBox,
            double baseZ,
            double rMinX, double rMaxX, double rMinY, double rMaxY,
            double sMinX, double sMaxX, double sMinY, double sMaxY,
            SiteMaterials materials,
            EntranceInfo entrance,
            Random rng,
            out int createdTreesCount,
            out double drivewayExtraRoadM2,
            out double walkwayExtraTileM2,
            List<CurveLoop> extraWalkways,
            List<CurveLoop> extraDriveways,
            out CurveLoop accountingParkingLoop)
        {
            ElementId siteCatId = new ElementId(BuiltInCategory.OST_Site);
            ElementId plantingCatId = new ElementId(BuiltInCategory.OST_Planting);

            double bMinX = buildingBox.Min.X;
            double bMaxX = buildingBox.Max.X;
            double bMinY = buildingBox.Min.Y;
            double bMaxY = buildingBox.Max.Y;
            double bCenterX = (bMinX + bMaxX) / 2.0;
            double entranceX = entrance.HasRealDoor ? entrance.EntrancePoint.X : bCenterX;

            // А. Компактный эко-въезд (4.5 м)
            double drvW = 4500.0 * MmToFeet;
            double drvMinX = sMinX + (1200.0 * MmToFeet);
            double drvMaxX = drvMinX + drvW;
            double drvMinY = sMinY;
            double drvMaxY = rMinY + (100.0 * MmToFeet);

            CurveLoop drvLoop = CreateRectangleLoop(drvMinX, drvMinY, drvMaxX, drvMaxY, baseZ - (450.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [drvLoop], 200.0 * MmToFeet, materials.AsphaltMaterialId,
                "ГП_3D: Эко-въезд на территорию кампуса — асфальт", "ГП-ВЪЕЗД");

            drivewayExtraRoadM2 = CalculateCurveLoopAreaM2(drvLoop);
            extraDriveways.Add(drvLoop);

            // Б. Компактная эко-парковка на 5 мест
            double spotW = ParkingSpotWidthMm * MmToFeet;
            double spotL = ParkingSpotLengthMm * MmToFeet;
            double parkStartX = drvMaxX + (1000.0 * MmToFeet);
            int spotCount = 5;
            double totalParkW = spotCount * spotW;

            double parkTopY = rMinY + (50.0 * MmToFeet);
            double parkBottomY = rMinY - spotL;

            accountingParkingLoop = CreateRectangleLoop(parkStartX, parkBottomY, parkStartX + totalParkW, parkTopY, baseZ - (450.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [accountingParkingLoop], 200.0 * MmToFeet, materials.AsphaltMaterialId,
                "ГП_3D: Эко-парковка кампуса — асфальт", "ГП-ПАРК");

            double curbW = CurbWidthMm * MmToFeet;
            CurveLoop pCurbOut = CreateRectangleLoop(parkStartX - curbW, parkBottomY - curbW, parkStartX + totalParkW + curbW, rMinY, baseZ - (400.0 * MmToFeet));
            CurveLoop pCurbIn = CreateRectangleLoop(parkStartX, parkBottomY, parkStartX + totalParkW, rMinY, baseZ - (400.0 * MmToFeet));
            pCurbIn.Flip();
            Create3DExtrusionDirectShape(doc, siteCatId, [pCurbOut, pCurbIn], 300.0 * MmToFeet, materials.CurbConcreteMaterialId,
                "ГП_3D: Бордюр эко-парковки", "ГП-БОРД");

            CreateParkingMarkingsAndStops(doc, siteCatId, parkStartX, parkBottomY, rMinY, spotCount, spotW, baseZ - (250.0 * MmToFeet), materials);

            // В. Прогулочные аллеи и дорожки кампуса
            walkwayExtraTileM2 = 0.0;

            // 1. Центральная аллея ко входу
            CurveLoop mainWalkLoop = CreateRectangleLoop(entranceX - (1200.0 * MmToFeet), sMinY, entranceX + (1200.0 * MmToFeet), rMinY, baseZ - (300.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [mainWalkLoop], 200.0 * MmToFeet, materials.PavingTileMaterialId,
                "ГП_3D: Центральная аллея кампуса — тротуарная плитка", "ГП-АЛЛЕЯ");
            walkwayExtraTileM2 += CalculateCurveLoopAreaM2(mainWalkLoop);
            extraWalkways.Add(mainWalkLoop);

            // 2. Северный прогулочный променад
            double promY0 = rMaxY + (2000.0 * MmToFeet);
            double promY1 = promY0 + (2200.0 * MmToFeet);
            CurveLoop northPromLoop = CreateRectangleLoop(sMinX + (3000.0 * MmToFeet), promY0, sMaxX - (3000.0 * MmToFeet), promY1, baseZ - (300.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [northPromLoop], 200.0 * MmToFeet, materials.PavingTileMaterialId,
                "ГП_3D: Северный прогулочный променад кампуса — плитка", "ГП-АЛЛЕЯ");
            walkwayExtraTileM2 += CalculateCurveLoopAreaM2(northPromLoop);
            extraWalkways.Add(northPromLoop);

            // 3. Центральная площадка отдыха на северном променаде
            CurveLoop alcoveLoop = CreateOctagonLoop(bCenterX, (promY0 + promY1) / 2.0, 2600.0 * MmToFeet, baseZ - (300.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [alcoveLoop], 200.0 * MmToFeet, materials.PavingTileMaterialId,
                "ГП_3D: Площадка отдыха северного променада — плитка", "ГП-ТРОТ");
            walkwayExtraTileM2 += CalculateCurveLoopAreaM2(alcoveLoop);
            extraWalkways.Add(alcoveLoop);

            // 4. Юго-восточная прогулочная дорожка
            CurveLoop seWalkLoop = CreateRectangleLoop(sMaxX - (4500.0 * MmToFeet), sMinY, sMaxX - (2500.0 * MmToFeet), rMinY, baseZ - (300.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [seWalkLoop], 200.0 * MmToFeet, materials.PavingTileMaterialId,
                "ГП_3D: Юго-восточная прогулочная дорожка — плитка", "ГП-АЛЛЕЯ");
            walkwayExtraTileM2 += CalculateCurveLoopAreaM2(seWalkLoop);
            extraWalkways.Add(seWalkLoop);

            // Г. Скамейки и светильники
            double groundZ = baseZ - (100.0 * MmToFeet);
            List<GeometryObject> mafSolids = [];
            mafSolids.AddRange(CreateBenchSolid(new XYZ(bCenterX - (1600.0 * MmToFeet), (promY0 + promY1) / 2.0, groundZ), Math.PI / 2.0, materials));
            mafSolids.AddRange(CreateBenchSolid(new XYZ(bCenterX + (1600.0 * MmToFeet), (promY0 + promY1) / 2.0, groundZ), -Math.PI / 2.0, materials));
            mafSolids.AddRange(CreateBenchSolid(new XYZ(entranceX - (2000.0 * MmToFeet), sMinY + (3500.0 * MmToFeet), groundZ), 0.0, materials));
            mafSolids.AddRange(CreateBenchSolid(new XYZ(entranceX + (2000.0 * MmToFeet), sMinY + (3500.0 * MmToFeet), groundZ), 0.0, materials));

            mafSolids.AddRange(CreateLampSolid(new XYZ(sMinX + (3500.0 * MmToFeet), (promY0 + promY1) / 2.0, groundZ), 1.0, 0.0, materials));
            mafSolids.AddRange(CreateLampSolid(new XYZ(sMaxX - (3500.0 * MmToFeet), (promY0 + promY1) / 2.0, groundZ), -1.0, 0.0, materials));
            mafSolids.AddRange(CreateLampSolid(new XYZ(entranceX - (1800.0 * MmToFeet), sMinY + (1000.0 * MmToFeet), groundZ), 1.0, 1.0, materials));
            mafSolids.AddRange(CreateLampSolid(new XYZ(entranceX + (1800.0 * MmToFeet), sMinY + (1000.0 * MmToFeet), groundZ), -1.0, 1.0, materials));

            DirectShape mafShape = DirectShape.CreateElement(doc, siteCatId);
            mafShape.SetShape(mafSolids);
            SetZoneMetadata(mafShape, "ГП_3D: Парковые скамейки и фонари кампуса", "ГП-МАФ");

            // Д. 4 естественные рощи деревьев с органическим разбросом (джиттером)
            List<XYZ> treePositions = [];
            double nwX = (sMinX + rMinX) / 2.0;
            double nwY = (rMaxY + sMaxY) / 2.0;
            double neX = (rMaxX + sMaxX) / 2.0;
            double neY = (rMaxY + sMaxY) / 2.0;
            double swX = (sMinX + rMinX) / 2.0;
            double swY = (sMinY + rMinY) / 2.0;
            double seX = (rMaxX + sMaxX) / 2.0;
            double seY = (sMinY + rMinY) / 2.0;

            (double CX, double CY, int Count)[] groves =
            [
                (nwX, nwY, 6),
                (neX, neY, 6),
                (swX, swY, 5),
                (seX, seY, 6)
            ];

            foreach ((double cx, double cy, int count) in groves)
            {
                for (int i = 0; i < count; i++)
                {
                    double angle = (2.0 * Math.PI * i) / count;
                    double radius = (2500.0 + (rng.NextDouble() * 1800.0)) * MmToFeet;
                    double jx = (rng.NextDouble() - 0.5) * (1400.0 * MmToFeet);
                    double jy = (rng.NextDouble() - 0.5) * (1400.0 * MmToFeet);
                    double tx = Math.Clamp(cx + (radius * Math.Cos(angle)) + jx, sMinX + (2000.0 * MmToFeet), sMaxX - (2000.0 * MmToFeet));
                    double ty = Math.Clamp(cy + (radius * Math.Sin(angle)) + jy, sMinY + (2000.0 * MmToFeet), sMaxY - (2000.0 * MmToFeet));
                    treePositions.Add(new XYZ(tx, ty, groundZ));
                }
            }

            List<GeometryObject> treeSolids = [];
            foreach (XYZ pos in treePositions)
            {
                double scale = 0.85 + (rng.NextDouble() * 0.35);
                treeSolids.AddRange(CreateTreeSolid(pos, materials, scale));
            }
            DirectShape tShape = DirectShape.CreateElement(doc, plantingCatId);
            tShape.SetShape(treeSolids);
            SetZoneMetadata(tShape, "ГП_3D: Естественные рощи паркового кампуса", "ГП-ДЕРЕВЬЯ");
            createdTreesCount = treePositions.Count;

            // Е. Ограждение с проемами для въезда и центральной калитки
            List<(double Start, double End, BuildingSide Side)> gateOpenings =
            [
                (drvMinX - (300.0 * MmToFeet), drvMaxX + (300.0 * MmToFeet), BuildingSide.South),
                (entranceX - (1600.0 * MmToFeet), entranceX + (1600.0 * MmToFeet), BuildingSide.South)
            ];
            Create3DSitePerimeterFenceWithOpenings(doc, siteCatId, groundZ, sMinX, sMaxX, sMinY, sMaxY, gateOpenings, materials);

            return spotCount;
        }

        private static int BuildCornerHubStyle(
            Document doc,
            Level level,
            BoundingBoxXYZ buildingBox,
            double baseZ,
            double rMinX, double rMaxX, double rMinY, double rMaxY,
            double sMinX, double sMaxX, double sMinY, double sMaxY,
            SiteMaterials materials,
            EntranceInfo entrance,
            Random rng,
            out int createdTreesCount,
            out double drivewayExtraRoadM2,
            out double walkwayExtraTileM2,
            List<CurveLoop> extraWalkways,
            List<CurveLoop> extraDriveways,
            out CurveLoop accountingParkingLoop)
        {
            ElementId siteCatId = new ElementId(BuiltInCategory.OST_Site);
            ElementId plantingCatId = new ElementId(BuiltInCategory.OST_Planting);

            double bMinX = buildingBox.Min.X;
            double bMaxX = buildingBox.Max.X;
            double bMinY = buildingBox.Min.Y;
            double bMaxY = buildingBox.Max.Y;
            double bCenterX = (bMinX + bMaxX) / 2.0;
            double entranceX = entrance.HasRealDoor ? entrance.EntrancePoint.X : bCenterX;

            // А. Угловой въезд в юго-западном углу
            double drvW = 5500.0 * MmToFeet;
            double drvMinX = sMinX + (1000.0 * MmToFeet);
            double drvMaxX = drvMinX + drvW;
            double drvMinY = sMinY;
            double drvMaxY = rMinY + (100.0 * MmToFeet);

            CurveLoop drvLoop = CreateRectangleLoop(drvMinX, drvMinY, drvMaxX, drvMaxY, baseZ - (450.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [drvLoop], 200.0 * MmToFeet, materials.AsphaltMaterialId,
                "ГП_3D: Угловой въезд на участок — асфальт", "ГП-ВЪЕЗД");

            drivewayExtraRoadM2 = CalculateCurveLoopAreaM2(drvLoop);
            extraDriveways.Add(drvLoop);

            // Б. Угловая парковка на 6 мест, примыкающая к въезду
            double spotW = ParkingSpotWidthMm * MmToFeet;
            double spotL = ParkingSpotLengthMm * MmToFeet;
            double parkStartX = drvMaxX + (600.0 * MmToFeet);
            int spotCount = 6;
            double totalParkW = spotCount * spotW;

            double parkTopY = rMinY + (50.0 * MmToFeet);
            double parkBottomY = rMinY - spotL;

            accountingParkingLoop = CreateRectangleLoop(parkStartX, parkBottomY, parkStartX + totalParkW, parkTopY, baseZ - (450.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [accountingParkingLoop], 200.0 * MmToFeet, materials.AsphaltMaterialId,
                "ГП_3D: Угловая автостоянка — асфальт", "ГП-ПАРК");

            double curbW = CurbWidthMm * MmToFeet;
            CurveLoop pCurbOut = CreateRectangleLoop(parkStartX - curbW, parkBottomY - curbW, parkStartX + totalParkW + curbW, rMinY, baseZ - (400.0 * MmToFeet));
            CurveLoop pCurbIn = CreateRectangleLoop(parkStartX, parkBottomY, parkStartX + totalParkW, rMinY, baseZ - (400.0 * MmToFeet));
            pCurbIn.Flip();
            Create3DExtrusionDirectShape(doc, siteCatId, [pCurbOut, pCurbIn], 300.0 * MmToFeet, materials.CurbConcreteMaterialId,
                "ГП_3D: Бордюр угловой автостоянки", "ГП-БОРД");

            CreateParkingMarkingsAndStops(doc, siteCatId, parkStartX, parkBottomY, rMinY, spotCount, spotW, baseZ - (250.0 * MmToFeet), materials);

            // В. Общественный сквер и восьмигранная площадь отдыха в северо-восточном углу
            walkwayExtraTileM2 = 0.0;
            double parkCenterX = (rMaxX + sMaxX) / 2.0;
            double parkCenterY = (rMaxY + sMaxY) / 2.0;
            double plazaRadius = 3600.0 * MmToFeet;

            CurveLoop octPlazaLoop = CreateOctagonLoop(parkCenterX, parkCenterY, plazaRadius, baseZ - (300.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [octPlazaLoop], 200.0 * MmToFeet, materials.PavingTileMaterialId,
                "ГП_3D: Восьмигранная площадь сквера — тротуарная плитка", "ГП-ТРОТ");
            walkwayExtraTileM2 += CalculateCurveLoopAreaM2(octPlazaLoop);
            extraWalkways.Add(octPlazaLoop);

            // Центральная круглая клумба площади
            double planterR = 1200.0 * MmToFeet;
            CurveLoop planterLoop = CreateOctagonLoop(parkCenterX, parkCenterY, planterR, baseZ - (100.0 * MmToFeet));
            Solid planterShrubs = GeometryCreationUtilities.CreateExtrusionGeometry(
                [planterLoop], XYZ.BasisZ, 450.0 * MmToFeet,
                new SolidOptions(materials.ShrubFoliageMaterialId, ElementId.InvalidElementId));
            DirectShape planterShape = DirectShape.CreateElement(doc, siteCatId);
            planterShape.SetShape([planterShrubs]);
            SetZoneMetadata(planterShape, "ГП_3D: Центральная клумба площади сквера", "ГП-КЛУМБА");

            // Лучевые аллеи от площади
            CurveLoop walkToBld = CreateRectangleLoop(rMaxX, parkCenterY - (1000.0 * MmToFeet), parkCenterX - (2500.0 * MmToFeet), parkCenterY + (1000.0 * MmToFeet), baseZ - (300.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [walkToBld], 200.0 * MmToFeet, materials.PavingTileMaterialId,
                "ГП_3D: Аллея от площади к зданию — плитка", "ГП-АЛЛЕЯ");
            walkwayExtraTileM2 += CalculateCurveLoopAreaM2(walkToBld);
            extraWalkways.Add(walkToBld);

            CurveLoop walkNorth = CreateRectangleLoop(parkCenterX - (1000.0 * MmToFeet), parkCenterY + (2500.0 * MmToFeet), parkCenterX + (1000.0 * MmToFeet), sMaxY, baseZ - (300.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [walkNorth], 200.0 * MmToFeet, materials.PavingTileMaterialId,
                "ГП_3D: Северная аллея сквера — плитка", "ГП-АЛЛЕЯ");
            walkwayExtraTileM2 += CalculateCurveLoopAreaM2(walkNorth);
            extraWalkways.Add(walkNorth);

            CurveLoop southWalk = CreateRectangleLoop(entranceX - (1200.0 * MmToFeet), sMinY, entranceX + (1200.0 * MmToFeet), rMinY, baseZ - (300.0 * MmToFeet));
            Create3DExtrusionDirectShape(doc, siteCatId, [southWalk], 200.0 * MmToFeet, materials.PavingTileMaterialId,
                "ГП_3D: Дорожка южного входа — плитка", "ГП-АЛЛЕЯ");
            walkwayExtraTileM2 += CalculateCurveLoopAreaM2(southWalk);
            extraWalkways.Add(southWalk);

            // Г. Скамейки ротонды и освещение
            double groundZ = baseZ - (100.0 * MmToFeet);
            List<GeometryObject> mafSolids = [];
            double benchDist = 2000.0 * MmToFeet;
            mafSolids.AddRange(CreateBenchSolid(new XYZ(parkCenterX - benchDist, parkCenterY, groundZ), Math.PI / 2.0, materials));
            mafSolids.AddRange(CreateBenchSolid(new XYZ(parkCenterX + benchDist, parkCenterY, groundZ), -Math.PI / 2.0, materials));
            mafSolids.AddRange(CreateBenchSolid(new XYZ(parkCenterX, parkCenterY - benchDist, groundZ), 0.0, materials));
            mafSolids.AddRange(CreateBenchSolid(new XYZ(parkCenterX, parkCenterY + benchDist, groundZ), Math.PI, materials));

            mafSolids.AddRange(CreateLampSolid(new XYZ(parkCenterX - (2800.0 * MmToFeet), parkCenterY - (2800.0 * MmToFeet), groundZ), 1.0, 1.0, materials));
            mafSolids.AddRange(CreateLampSolid(new XYZ(parkCenterX + (2800.0 * MmToFeet), parkCenterY + (2800.0 * MmToFeet), groundZ), -1.0, -1.0, materials));
            mafSolids.AddRange(CreateLampSolid(new XYZ(drvMinX - (400.0 * MmToFeet), sMinY + (2000.0 * MmToFeet), groundZ), 1.0, 1.0, materials));

            DirectShape mafShape = DirectShape.CreateElement(doc, siteCatId);
            mafShape.SetShape(mafSolids);
            SetZoneMetadata(mafShape, "ГП_3D: Скамейки ротонды и освещение сквера", "ГП-МАФ");

            // Д. Центральное дерево и живописный массив сквера
            List<XYZ> treePositions =
            [
                new XYZ(parkCenterX, parkCenterY, groundZ),
                new XYZ(parkCenterX - (4200.0 * MmToFeet), parkCenterY + (1000.0 * MmToFeet), groundZ),
                new XYZ(parkCenterX + (4200.0 * MmToFeet), parkCenterY - (1000.0 * MmToFeet), groundZ),
                new XYZ(parkCenterX + (2000.0 * MmToFeet), parkCenterY + (4200.0 * MmToFeet), groundZ),
                new XYZ(parkCenterX - (2000.0 * MmToFeet), parkCenterY - (4200.0 * MmToFeet), groundZ),
                new XYZ(rMinX - (2500.0 * MmToFeet), (rMinY + rMaxY) / 2.0, groundZ),
                new XYZ(rMinX - (2500.0 * MmToFeet), rMaxY - (2000.0 * MmToFeet), groundZ),
                new XYZ(bCenterX - (4000.0 * MmToFeet), sMaxY - (2500.0 * MmToFeet), groundZ),
                new XYZ(bCenterX + (4000.0 * MmToFeet), sMaxY - (2500.0 * MmToFeet), groundZ),
                new XYZ(entranceX + (3000.0 * MmToFeet), sMinY + (2500.0 * MmToFeet), groundZ)
            ];

            List<GeometryObject> treeSolids = [];
            foreach (XYZ pos in treePositions)
            {
                treeSolids.AddRange(CreateTreeSolid(pos, materials, scale: pos == treePositions[0] ? 1.3 : 1.0));
            }
            DirectShape tShape = DirectShape.CreateElement(doc, plantingCatId);
            tShape.SetShape(treeSolids);
            SetZoneMetadata(tShape, "ГП_3D: Деревья паркового сквера и ротонды", "ГП-ДЕРЕВЬЯ");
            createdTreesCount = treePositions.Count;

            // Е. Ограждение
            List<(double Start, double End, BuildingSide Side)> gateOpenings =
            [
                (drvMinX - (300.0 * MmToFeet), drvMaxX + (300.0 * MmToFeet), BuildingSide.South),
                (entranceX - (1500.0 * MmToFeet), entranceX + (1500.0 * MmToFeet), BuildingSide.South),
                (parkCenterX - (1500.0 * MmToFeet), parkCenterX + (1500.0 * MmToFeet), BuildingSide.North)
            ];
            Create3DSitePerimeterFenceWithOpenings(doc, siteCatId, groundZ, sMinX, sMaxX, sMinY, sMaxY, gateOpenings, materials);

            return spotCount;
        }

        private static List<GeometryObject> CreateTreeSolid(XYZ pos, SiteMaterials materials, double scale = 1.0)
        {
            List<GeometryObject> treeParts = [];
            double trunkR = 180.0 * MmToFeet * scale;
            double trunkH = 2200.0 * MmToFeet * scale;
            double cLowerR = 1700.0 * MmToFeet * scale;
            double cLowerH = 1400.0 * MmToFeet * scale;
            double cMidR = 1300.0 * MmToFeet * scale;
            double cMidH = 1300.0 * MmToFeet * scale;
            double cTopR = 750.0 * MmToFeet * scale;
            double cTopH = 900.0 * MmToFeet * scale;

            CurveLoop trunkLoop = CreateOctagonLoop(pos.X, pos.Y, trunkR, pos.Z - (40.0 * MmToFeet));
            treeParts.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [trunkLoop], XYZ.BasisZ, trunkH,
                new SolidOptions(materials.TreeTrunkMaterialId, ElementId.InvalidElementId)));

            CurveLoop cLowLoop = CreateOctagonLoop(pos.X, pos.Y, cLowerR, pos.Z + (1800.0 * MmToFeet * scale));
            treeParts.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [cLowLoop], XYZ.BasisZ, cLowerH,
                new SolidOptions(materials.TreeFoliageMaterialId, ElementId.InvalidElementId)));

            CurveLoop cMidLoop = CreateOctagonLoop(pos.X, pos.Y, cMidR, pos.Z + (3200.0 * MmToFeet * scale));
            treeParts.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [cMidLoop], XYZ.BasisZ, cMidH,
                new SolidOptions(materials.ShrubFoliageMaterialId, ElementId.InvalidElementId)));

            CurveLoop cTopLoop = CreateOctagonLoop(pos.X, pos.Y, cTopR, pos.Z + (4500.0 * MmToFeet * scale));
            treeParts.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [cTopLoop], XYZ.BasisZ, cTopH,
                new SolidOptions(materials.TreeFoliageMaterialId, ElementId.InvalidElementId)));

            return treeParts;
        }

        private static List<GeometryObject> CreateLampSolid(XYZ pos, double dx, double dy, SiteMaterials materials)
        {
            List<GeometryObject> lampParts = [];
            CurveLoop basePedestal = CreateOctagonLoop(pos.X, pos.Y, 160.0 * MmToFeet, pos.Z);
            lampParts.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [basePedestal], XYZ.BasisZ, 600.0 * MmToFeet,
                new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));

            CurveLoop poleLoop = CreateOctagonLoop(pos.X, pos.Y, 75.0 * MmToFeet, pos.Z + (600.0 * MmToFeet));
            lampParts.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [poleLoop], XYZ.BasisZ, 3900.0 * MmToFeet,
                new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));

            double headX = pos.X + (dx * 450.0 * MmToFeet);
            double headY = pos.Y + (dy * 450.0 * MmToFeet);
            CurveLoop headLoop = CreateRectangleLoop(
                Math.Min(pos.X, headX) - (140.0 * MmToFeet),
                Math.Min(pos.Y, headY) - (140.0 * MmToFeet),
                Math.Max(pos.X, headX) + (140.0 * MmToFeet),
                Math.Max(pos.Y, headY) + (140.0 * MmToFeet),
                pos.Z + (4350.0 * MmToFeet));
            lampParts.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [headLoop], XYZ.BasisZ, 180.0 * MmToFeet,
                new SolidOptions(materials.LampGlowMaterialId, ElementId.InvalidElementId)));

            return lampParts;
        }

        private static List<GeometryObject> CreateBenchSolid(XYZ center, double angleRad, SiteMaterials materials)
        {
            List<GeometryObject> benchParts = [];
            double halfLen = 900.0 * MmToFeet;
            double halfWid = 250.0 * MmToFeet;
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);

            XYZ p1 = center + new XYZ((-halfLen * cos) - (-halfWid * sin), (-halfLen * sin) + (-halfWid * cos), 400.0 * MmToFeet);
            XYZ p2 = center + new XYZ((halfLen * cos) - (-halfWid * sin), (halfLen * sin) + (-halfWid * cos), 400.0 * MmToFeet);
            XYZ p3 = center + new XYZ((halfLen * cos) - (halfWid * sin), (halfLen * sin) + (halfWid * cos), 400.0 * MmToFeet);
            XYZ p4 = center + new XYZ((-halfLen * cos) - (halfWid * sin), (-halfLen * sin) + (halfWid * cos), 400.0 * MmToFeet);

            CurveLoop seatLoop = new CurveLoop();
            seatLoop.Append(Line.CreateBound(p1, p2));
            seatLoop.Append(Line.CreateBound(p2, p3));
            seatLoop.Append(Line.CreateBound(p3, p4));
            seatLoop.Append(Line.CreateBound(p4, p1));

            benchParts.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [seatLoop], XYZ.BasisZ, 80.0 * MmToFeet,
                new SolidOptions(materials.WoodBenchMaterialId, ElementId.InvalidElementId)));

            double backOffset = 220.0 * MmToFeet;
            double backThickness = 80.0 * MmToFeet;
            XYZ bCenter = center + new XYZ(-sin * backOffset, cos * backOffset, 480.0 * MmToFeet);
            XYZ bp1 = bCenter + new XYZ((-halfLen * cos) - (-backThickness * sin), (-halfLen * sin) + (-backThickness * cos), 0.0);
            XYZ bp2 = bCenter + new XYZ((halfLen * cos) - (-backThickness * sin), (halfLen * sin) + (-backThickness * cos), 0.0);
            XYZ bp3 = bCenter + new XYZ((halfLen * cos) - (backThickness * sin), (halfLen * sin) + (backThickness * cos), 0.0);
            XYZ bp4 = bCenter + new XYZ((-halfLen * cos) - (backThickness * sin), (-halfLen * sin) + (backThickness * cos), 0.0);

            CurveLoop backLoop = new CurveLoop();
            backLoop.Append(Line.CreateBound(bp1, bp2));
            backLoop.Append(Line.CreateBound(bp2, bp3));
            backLoop.Append(Line.CreateBound(bp3, bp4));
            backLoop.Append(Line.CreateBound(bp4, bp1));

            benchParts.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [backLoop], XYZ.BasisZ, 380.0 * MmToFeet,
                new SolidOptions(materials.WoodBenchMaterialId, ElementId.InvalidElementId)));

            return benchParts;
        }

        private static void CreateParkingMarkingsAndStops(
            Document doc,
            ElementId siteCatId,
            double parkingStartX,
            double parkingBottomY,
            double rMinY,
            int spotCount,
            double spotWidthFt,
            double roadTopZ,
            SiteMaterials materials)
        {
            List<GeometryObject> markings = [];
            double stripeHalfWidthFt = 60.0 * MmToFeet;
            double stripeHeightFt = 6.0 * MmToFeet;
            double normalizedRoadTopZ = s_currentSiteBaseZ - (102.0 * MmToFeet);
            double stripeZ = normalizedRoadTopZ + (1.0 * MmToFeet);
            double stopperZ = normalizedRoadTopZ;

            for (int i = 0; i <= spotCount; i++)
            {
                double sx = parkingStartX + (i * spotWidthFt);
                CurveLoop stripeLoop = CreateRectangleLoop(
                    sx - stripeHalfWidthFt,
                    parkingBottomY + (150.0 * MmToFeet),
                    sx + stripeHalfWidthFt,
                    rMinY - (100.0 * MmToFeet),
                    stripeZ);

                markings.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    [stripeLoop], XYZ.BasisZ, stripeHeightFt,
                    new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));

                if (i < spotCount)
                {
                    double spotCenterX = sx + (spotWidthFt / 2.0);
                    double stopperHalfLen = 800.0 * MmToFeet;
                    double stopperY0 = parkingBottomY + (650.0 * MmToFeet);
                    double stopperY1 = stopperY0 + (180.0 * MmToFeet);

                    CurveLoop stopperLoop = CreateRectangleLoop(
                        spotCenterX - stopperHalfLen,
                        stopperY0,
                        spotCenterX + stopperHalfLen,
                        stopperY1,
                        stopperZ);

                    markings.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                        [stopperLoop], XYZ.BasisZ, 120.0 * MmToFeet,
                        new SolidOptions(materials.YellowStopperMaterialId, ElementId.InvalidElementId)));
                }
            }

            if (markings.Count > 0)
            {
                DirectShape ds = DirectShape.CreateElement(doc, siteCatId);
                ds.SetShape(markings);
                SetZoneMetadata(ds, "ГП_3D: Разметка машино-мест и колесоотбойники", "ГП-РАЗМ");
            }
        }

        private static void CreateZebraCrossing(
            List<GeometryObject> markings,
            double centerX,
            double startY,
            double endY,
            double widthFt,
            double stripeZ,
            SiteMaterials materials)
        {
            double stepFt = 700.0 * MmToFeet;
            double lenFt = 400.0 * MmToFeet;
            double heightFt = 6.0 * MmToFeet;
            double halfW = widthFt / 2.0;
            double normalizedStripeZ = s_currentSiteBaseZ - (101.0 * MmToFeet);

            double yMin = Math.Min(startY, endY);
            double yMax = Math.Max(startY, endY);

            for (double y = yMin; y + lenFt <= yMax; y += stepFt)
            {
                CurveLoop stripe = CreateRectangleLoop(
                    centerX - halfW, y,
                    centerX + halfW, y + lenFt,
                    normalizedStripeZ);

                markings.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    [stripe], XYZ.BasisZ, heightFt,
                    new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));
            }
        }

        private static void Create3DSitePerimeterFenceWithOpenings(
            Document doc,
            ElementId siteCatId,
            double baseZ,
            double sMinX, double sMaxX, double sMinY, double sMaxY,
            List<(double Start, double End, BuildingSide Side)> gateOpenings,
            SiteMaterials materials)
        {
            List<GeometryObject> fenceSolids = [];
            double plinthW = 180.0 * MmToFeet;
            double plinthH = 320.0 * MmToFeet;
            double railH = 80.0 * MmToFeet;
            double postR = 70.0 * MmToFeet;

            static List<(double A, double B)> SplitInterval(double min, double max, List<(double Start, double End)> openings)
            {
                List<(double A, double B)> result = [];
                double curr = min;
                var sorted = openings
                    .Where(o => o.End > min && o.Start < max)
                    .OrderBy(o => o.Start)
                    .ToList();

                foreach (var op in sorted)
                {
                    double opStart = Math.Clamp(op.Start, min, max);
                    double opEnd = Math.Clamp(op.End, min, max);
                    if (opStart > curr + (300.0 * MmToFeet))
                    {
                        result.Add((curr, opStart));
                    }
                    curr = Math.Max(curr, opEnd);
                }
                if (max > curr + (300.0 * MmToFeet))
                {
                    result.Add((curr, max));
                }
                return result;
            }

            var southOpenings = gateOpenings.Where(g => g.Side == BuildingSide.South).Select(g => (g.Start, g.End)).ToList();
            foreach (var seg in SplitInterval(sMinX, sMaxX, southOpenings))
            {
                AddFenceSegment(fenceSolids, seg.A, sMinY, seg.B, sMinY + plinthW, baseZ, plinthH, railH, postR, materials, isHorizontal: true);
            }

            var northOpenings = gateOpenings.Where(g => g.Side == BuildingSide.North).Select(g => (g.Start, g.End)).ToList();
            foreach (var seg in SplitInterval(sMinX, sMaxX, northOpenings))
            {
                AddFenceSegment(fenceSolids, seg.A, sMaxY - plinthW, seg.B, sMaxY, baseZ, plinthH, railH, postR, materials, isHorizontal: true);
            }

            var westOpenings = gateOpenings.Where(g => g.Side == BuildingSide.West).Select(g => (g.Start, g.End)).ToList();
            foreach (var seg in SplitInterval(sMinY, sMaxY, westOpenings))
            {
                AddFenceSegment(fenceSolids, sMinX, seg.A, sMinX + plinthW, seg.B, baseZ, plinthH, railH, postR, materials, isHorizontal: false);
            }

            var eastOpenings = gateOpenings.Where(g => g.Side == BuildingSide.East).Select(g => (g.Start, g.End)).ToList();
            foreach (var seg in SplitInterval(sMinY, sMaxY, eastOpenings))
            {
                AddFenceSegment(fenceSolids, sMaxX - plinthW, seg.A, sMaxX, seg.B, baseZ, plinthH, railH, postR, materials, isHorizontal: false);
            }

            if (fenceSolids.Count > 0)
            {
                DirectShape fenceShape = DirectShape.CreateElement(doc, siteCatId);
                fenceShape.SetShape(fenceSolids);
                SetZoneMetadata(fenceShape, "ГП_3D: Ограждение территории участка (забор 1.8 м с въездными и пешеходными воротами)", "ГП-ЗАБОР");
            }
        }

        private static void AddFenceSegment(
            List<GeometryObject> fenceSolids,
            double x0, double y0, double x1, double y1,
            double baseZ, double plinthH, double railH, double postR,
            SiteMaterials materials,
            bool isHorizontal)
        {
            if (Math.Abs(x1 - x0) < 100.0 * MmToFeet && Math.Abs(y1 - y0) < 100.0 * MmToFeet)
                return;

            CurveLoop plinthLoop = CreateRectangleLoop(x0, y0, x1, y1, baseZ - (40.0 * MmToFeet));
            fenceSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [plinthLoop], XYZ.BasisZ, plinthH,
                new SolidOptions(materials.CurbConcreteMaterialId, ElementId.InvalidElementId)));

            CurveLoop midRail = CreateRectangleLoop(x0, y0, x1, y1, baseZ + (1050.0 * MmToFeet));
            fenceSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [midRail], XYZ.BasisZ, railH,
                new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));

            CurveLoop topRail = CreateRectangleLoop(x0, y0, x1, y1, baseZ + (1650.0 * MmToFeet));
            fenceSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [topRail], XYZ.BasisZ, railH,
                new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));

            double postStep = 3500.0 * MmToFeet;
            if (isHorizontal)
            {
                double centerY = (y0 + y1) / 2.0;
                for (double px = x0; px <= x1; px += postStep)
                {
                    CurveLoop postLoop = CreateOctagonLoop(px, centerY, postR, baseZ);
                    fenceSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                        [postLoop], XYZ.BasisZ, 1850.0 * MmToFeet,
                        new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));
                }
            }
            else
            {
                double centerX = (x0 + x1) / 2.0;
                for (double py = y0; py <= y1; py += postStep)
                {
                    CurveLoop postLoop = CreateOctagonLoop(centerX, py, postR, baseZ);
                    fenceSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                        [postLoop], XYZ.BasisZ, 1850.0 * MmToFeet,
                        new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));
                }
            }
        }

        private static void Create3DExtrusionDirectShape(
            Document doc,
            ElementId categoryId,
            IList<CurveLoop> loops,
            double heightFt,
            ElementId materialId,
            string comment,
            string mark)
        {
            try
            {
                // Нормализуем Z-отметки всех дорожных и площадочных покрытий к единому основанию (baseZ - 220 мм)
                // и строго упорядоченным верхам: Газон (-115 мм) < Асфальт (-102 мм) < Плитка/Площадки (-88 мм) < Бордюр (-75 мм) < Терраса (-70 мм)
                double? targetTopOffsetMm = mark switch
                {
                    "ГП-ГАЗ" => -115.0,
                    "ГП-ДОР" or "ГП-ПАРК" or "ГП-ВЪЕЗД" or "4" or "5" or "11" => -102.0,
                    "ГП-ТРОТ" or "ГП-АЛЛЕЯ" or "3" or "6" or "7" or "8" or "10" or "12" or "14" or "15" => -88.0,
                    "ГП-БОРД" or "ГП-БОРД-1" or "ГП-БОРД-2" => -75.0,
                    "2" => -70.0,
                    _ => -100.0
                };

                IList<CurveLoop> extrusionLoops = loops;
                double extrusionHeight = heightFt;

                if (targetTopOffsetMm.HasValue && loops.Count > 0)
                {
                    double slabBottomZ = s_currentSiteBaseZ - (220.0 * MmToFeet);
                    double slabTopZ = s_currentSiteBaseZ + (targetTopOffsetMm.Value * MmToFeet);
                    double newHeight = Math.Max(25.0 * MmToFeet, slabTopZ - slabBottomZ);

                    List<CurveLoop> normalizedLoops = [];
                    foreach (CurveLoop loop in loops)
                    {
                        normalizedLoops.Add(ShiftLoopZ(loop, slabBottomZ));
                    }

                    extrusionLoops = normalizedLoops;
                    extrusionHeight = newHeight;
                }

                Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(
                    extrusionLoops,
                    XYZ.BasisZ,
                    extrusionHeight,
                    new SolidOptions(materialId, ElementId.InvalidElementId));

                DirectShape ds = DirectShape.CreateElement(doc, categoryId);
                ds.SetShape([solid]);
                SetZoneMetadata(ds, comment, mark);
            }
            catch
            {
                // Резервный вариант, если контур содержит вырожденные сегменты
            }
        }

        private static ElementId GetOrCreateSiteFloorType(Document doc, ElementId baseFloorTypeId, string name, ElementId materialId)
        {
            FloorType? existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .FirstOrDefault(ft => ft.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.Id;

            if (doc.GetElement(baseFloorTypeId) is not FloorType baseType) return baseFloorTypeId;

            try
            {
                FloorType newType = (FloorType)baseType.Duplicate(name);
                CompoundStructure cs = newType.GetCompoundStructure();
                if (cs != null && materialId != ElementId.InvalidElementId)
                {
                    var layers = cs.GetLayers();
                    if (layers.Count > 0)
                    {
                        layers[0].MaterialId = materialId;
                        cs.SetLayers(layers);
                        newType.SetCompoundStructure(cs);
                    }
                }
                return newType.Id;
            }
            catch
            {
                return baseFloorTypeId;
            }
        }

        private static List<ElementId> CreateScheduleAccountingFloors(
            Document doc,
            Level level,
            CurveLoop buildingLoop,
            CurveLoop blindAreaLoop,
            CurveLoop roadLoop,
            CurveLoop siteBoundaryLoop,
            CurveLoop parkingBayLoop,
            SiteMaterials materials,
            IList<CurveLoop>? extraWalkways = null,
            IList<CurveLoop>? extraDriveways = null)
        {
            List<ElementId> createdFloorIds = [];
            ElementId baseFloorTypeId = Floor.GetDefaultFloorType(doc, false);
            if (baseFloorTypeId == ElementId.InvalidElementId)
            {
                baseFloorTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(FloorType))
                    .FirstElementId();
            }

            if (baseFloorTypeId == ElementId.InvalidElementId)
            {
                return createdFloorIds;
            }

            ElementId floorBuildingId = GetOrCreateSiteFloorType(doc, baseFloorTypeId, "ГП_Площадь_Застройки", ElementId.InvalidElementId);
            ElementId floorTileId = GetOrCreateSiteFloorType(doc, baseFloorTypeId, "ГП_Покрытие_ТротуарнаяПлитка", materials.PavingTileMaterialId);
            ElementId floorAsphaltId = GetOrCreateSiteFloorType(doc, baseFloorTypeId, "ГП_Покрытие_Асфальт", materials.AsphaltMaterialId);
            ElementId floorGrassId = GetOrCreateSiteFloorType(doc, baseFloorTypeId, "ГП_Покрытие_Газон", materials.GrassMaterialId);

            double pedZOffset = -100.0 * MmToFeet;
            double roadZOffset = -250.0 * MmToFeet;

            void AddFloor(IList<CurveLoop> loops, ElementId ftId, string comment, string mark, double offset)
            {
                Floor? f = TryCreateFloorZone(doc, loops, ftId, level.Id, comment, mark, offset);
                if (f != null)
                {
                    createdFloorIds.Add(f.Id);
                }
            }

            AddFloor([CurveLoop.CreateViaCopy(buildingLoop)], floorBuildingId,
                "ГП: 1. Площадь застройки здания", "1", pedZOffset);

            CurveLoop blindOut = CurveLoop.CreateViaCopy(blindAreaLoop);
            CurveLoop blindIn = CurveLoop.CreateViaCopy(buildingLoop);
            blindIn.Flip();
            AddFloor([blindOut, blindIn], floorTileId,
                "ГП: 2. Тротуары и отмостка — плитка (+1500 мм)", "2", pedZOffset);

            if (extraWalkways != null)
            {
                int walkIdx = 1;
                foreach (CurveLoop wLoop in extraWalkways)
                {
                    AddFloor([CurveLoop.CreateViaCopy(wLoop)], floorTileId,
                        $"ГП: 2. Площади, аллеи и дорожки #{walkIdx++} — плитка", "2", pedZOffset);
                }
            }

            CurveLoop roadOut = CurveLoop.CreateViaCopy(roadLoop);
            CurveLoop roadIn = CurveLoop.CreateViaCopy(blindAreaLoop);
            roadIn.Flip();
            AddFloor([roadOut, roadIn], floorAsphaltId,
                "ГП: 3. Проезд и автодорога — асфальт (+6000 мм)", "3", roadZOffset);

            if (extraDriveways != null)
            {
                int drvIdx = 1;
                foreach (CurveLoop dLoop in extraDriveways)
                {
                    AddFloor([CurveLoop.CreateViaCopy(dLoop)], floorAsphaltId,
                        $"ГП: 3. Въезд на участок #{drvIdx++} — асфальт", "3", roadZOffset);
                }
            }

            AddFloor([CurveLoop.CreateViaCopy(parkingBayLoop)], floorAsphaltId,
                "ГП: 4. Парковочный карман (2.5×5.0 м) — асфальт", "4", roadZOffset);

            CurveLoop siteOut = CurveLoop.CreateViaCopy(siteBoundaryLoop);
            CurveLoop siteIn = CurveLoop.CreateViaCopy(roadLoop);
            siteIn.Flip();
            AddFloor([siteOut, siteIn], floorGrassId,
                "ГП: 5. Газон и озеленение (+15000 мм)", "5", pedZOffset);

            return createdFloorIds;
        }

        private static void CleanupPreviousSiteElements(Document doc)
        {
            List<ElementId> oldIds = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e is DirectShape or Toposolid or Floor)
                .Where(e =>
                {
                    string? comment = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                    return comment != null &&
                           (comment.StartsWith("ГП:", StringComparison.OrdinalIgnoreCase) ||
                            comment.StartsWith("ГП_2D:", StringComparison.OrdinalIgnoreCase) ||
                            comment.StartsWith("ГП_3D:", StringComparison.OrdinalIgnoreCase));
                })
                .Select(e => e.Id)
                .ToList();

            if (oldIds.Count > 0)
            {
                try
                {
                    doc.Delete(oldIds);
                    doc.Regenerate();
                }
                catch
                {
                    // Продолжаем построение
                }
            }
        }

        // =========================================================================
        // СОЗДАНИЕ 3D-МАТЕРИАЛОВ И НАСТРОЙКА ЦВЕТОВОЙ ГРАФИКИ ВИДОВ (2D + 3D)
        // =========================================================================

        private static (ElementId SolidId, ElementId TileGridId, ElementId WoodPlankId) FindKeyFillPatterns(Document doc)
        {
            ElementId solidId = ElementId.InvalidElementId;
            ElementId tileGridId = ElementId.InvalidElementId;
            ElementId woodId = ElementId.InvalidElementId;

            var patterns = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .ToList();

            foreach (var fpElem in patterns)
            {
                var fp = fpElem.GetFillPattern();
                if (fp == null) continue;

                if (solidId == ElementId.InvalidElementId && fp.IsSolidFill)
                {
                    solidId = fpElem.Id;
                }

                string name = fpElem.Name;
                if (tileGridId == ElementId.InvalidElementId && fp.Target == FillPatternTarget.Model)
                {
                    if (name.Contains("Сетка", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Плитк", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Tile", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Grid", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Квадрат", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Брусчат", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Cross", StringComparison.OrdinalIgnoreCase))
                    {
                        tileGridId = fpElem.Id;
                    }
                }

                if (woodId == ElementId.InvalidElementId && fp.Target == FillPatternTarget.Model)
                {
                    if (name.Contains("Дерев", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Доск", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Wood", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Plank", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Полосы", StringComparison.OrdinalIgnoreCase))
                    {
                        woodId = fpElem.Id;
                    }
                }
            }

            if (tileGridId == ElementId.InvalidElementId)
            {
                tileGridId = patterns.FirstOrDefault(p =>
                    p.GetFillPattern()?.Target == FillPatternTarget.Model &&
                    (p.Name.Contains("Cross", StringComparison.OrdinalIgnoreCase) ||
                     p.Name.Contains("Крест", StringComparison.OrdinalIgnoreCase)))?.Id
                    ?? ElementId.InvalidElementId;
            }

            return (solidId, tileGridId, woodId);
        }

        private static void TryAttachAppearanceAsset(Document doc, Material mat, string keyword, string fallbackKeyword = "")
        {
            if (mat.AppearanceAssetId != ElementId.InvalidElementId)
            {
                return;
            }

            try
            {
                var assets = new FilteredElementCollector(doc)
                    .OfClass(typeof(AppearanceAssetElement))
                    .Cast<AppearanceAssetElement>()
                    .ToList();

                AppearanceAssetElement? match = assets.FirstOrDefault(a =>
                    a.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);

                if (match == null && !string.IsNullOrEmpty(fallbackKeyword))
                {
                    match = assets.FirstOrDefault(a =>
                        a.Name.IndexOf(fallbackKeyword, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (match != null)
                {
                    mat.AppearanceAssetId = match.Id;
                }
            }
            catch
            {
            }
        }

        private static SiteMaterials CreateOrUpdateSiteMaterials(Document doc, SitePlanEditorConfig? config = null)
        {
            (ElementId solidPatternId, ElementId tileGridPatternId, ElementId woodPatternId) = FindKeyFillPatterns(doc);

            // Для тротуарной плитки используем узор сетки швов (Tile Grid) с цветом шва
            ElementId paverSurfacePattern = tileGridPatternId != ElementId.InvalidElementId ? tileGridPatternId : solidPatternId;
            Color paverJointColor = new Color(165, 160, 150);

            // Для деревянных настилов используем текстуру досок
            ElementId woodSurfacePattern = woodPatternId != ElementId.InvalidElementId ? woodPatternId : solidPatternId;
            Color woodJointColor = new Color(125, 75, 35);

            Color grassColor = config?.GreenColor ?? new Color(88, 168, 72);
            Color asphaltColor = config?.AsphaltColor ?? new Color(55, 58, 64);
            Color pavingColor = config?.PavingColor ?? new Color(228, 222, 210);
            Color curbColor = config?.CurbColor ?? new Color(192, 196, 202);

            return new SiteMaterials(
                GrassMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Газон (Трава)", grassColor, solidPatternId, null, solidPatternId, "Трава", "Grass"),
                AsphaltMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Асфальтобетон", asphaltColor, solidPatternId, null, solidPatternId, "Асфальт", "Asphalt"),
                PavingTileMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Тротуарная плитка", pavingColor, paverSurfacePattern, paverJointColor, solidPatternId, "Плитка", "Tile"),
                CurbConcreteMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Бордюр бетонный", curbColor, solidPatternId, null, solidPatternId, "Бетон", "Concrete"),
                WhiteStripeMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Разметка дорожная", new Color(255, 255, 255), solidPatternId, null, solidPatternId, "Белый", "White"),
                YellowStopperMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Колесоотбойник", new Color(245, 182, 25), solidPatternId, null, solidPatternId, "Желтый", "Yellow"),
                TreeTrunkMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Кора дерева", new Color(105, 70, 42), solidPatternId, null, solidPatternId, "Дерево", "Bark"),
                TreeFoliageMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Крона деревьев", new Color(50, 130, 45), solidPatternId, null, solidPatternId, "Листва", "Foliage"),
                ShrubFoliageMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Кустарник", new Color(75, 155, 65), solidPatternId, null, solidPatternId, "Кустарник", "Plant"),
                MetalDarkMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Металл опор", new Color(50, 55, 60), solidPatternId, null, solidPatternId, "Металл", "Metal"),
                LampGlowMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Светильник", new Color(255, 245, 200), solidPatternId, null, solidPatternId, "Стекло", "Glass"),
                WoodBenchMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Дерево скамеек и террас", new Color(175, 115, 60), woodSurfacePattern, woodJointColor, solidPatternId, "Дерево", "Wood"),
                PlaygroundYellowMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Детская площадка", new Color(245, 165, 35), solidPatternId, null, solidPatternId, "Резина", "Rubber"),
                SportsCourtOrangeMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Спортивная площадка", new Color(65, 140, 210), solidPatternId, null, solidPatternId, "Покрытие", "Court"),
                RedBoundaryMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Красная линия границы", new Color(220, 38, 38), solidPatternId, null, solidPatternId),
                WasteSiteMaterialId: GetOrCreateColoredMaterial(
                    doc, "ГП_Площадка ТБО", new Color(145, 140, 135), solidPatternId, null, solidPatternId, "Бетон", "Granite"));
        }

        private static ElementId GetOrCreateColoredMaterial(
            Document doc,
            string name,
            Color color,
            ElementId surfacePatternId,
            Color? surfacePatternColor = null,
            ElementId? cutPatternId = null,
            string? appearanceKeyword = null,
            string? fallbackAppearanceKeyword = null)
        {
            Material? mat = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (mat == null)
            {
                ElementId matId = Material.Create(doc, name);
                mat = doc.GetElement(matId) as Material;
            }

            if (mat == null)
            {
                return ElementId.InvalidElementId;
            }

            mat.Color = color;
            mat.UseRenderAppearanceForShading = false;

            if (surfacePatternId != null && surfacePatternId != ElementId.InvalidElementId)
            {
                try
                {
                    mat.SurfaceForegroundPatternId = surfacePatternId;
                    mat.SurfaceForegroundPatternColor = surfacePatternColor ?? color;
                }
                catch
                {
                }
            }

            if (cutPatternId != null && cutPatternId != ElementId.InvalidElementId)
            {
                try
                {
                    mat.CutForegroundPatternId = cutPatternId;
                    mat.CutForegroundPatternColor = color;
                }
                catch
                {
                }
            }

            if (!string.IsNullOrEmpty(appearanceKeyword))
            {
                TryAttachAppearanceAsset(doc, mat, appearanceKeyword, fallbackAppearanceKeyword ?? "");
            }

            return mat.Id;
        }

        /// <summary>
        /// Применяет графическую заливку (OverrideGraphicSettings) к созданным 3D-элементам генплана,
        /// чтобы они гарантированно отображались в цвете на плане и в 3D независимо от шаблонов вида.
        /// </summary>
        private static void ApplyColorOverridesToSiteView(
            Document doc,
            View view,
            SiteMaterials materials,
            SitePlanEditorConfig? config = null)
        {
            ElementId solidPatternId = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill)?.Id
                ?? ElementId.InvalidElementId;

            List<Element> siteElements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e is DirectShape or Floor or Toposolid)
                .ToList();

            Color buildingFill = config?.BuildingColor ?? new Color(255, 255, 255);
            Color asphaltFill = config?.AsphaltColor ?? new Color(90, 95, 105);
            Color pavingFill = config?.PavingColor ?? new Color(228, 222, 210);
            Color greenFill = config?.GreenColor ?? new Color(152, 210, 136);
            Color curbFill = config?.CurbColor ?? new Color(185, 190, 195);

            foreach (Element elem in siteElements)
            {
                string? mark = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                if (string.IsNullOrEmpty(mark))
                {
                    continue;
                }

                (Color? fillColor, ElementId matId) = mark switch
                {
                    "1" or "ГП-ЗДАНИЕ-БЕЛЫЙ" => (buildingFill, materials.WhiteStripeMaterialId),
                    "ГП-ГАЗ" => (greenFill, materials.GrassMaterialId),
                    "ГП-ДОР" or "ГП-ВЪЕЗД" or "ГП-ПАРК" or "4" or "5" or "11" => (asphaltFill, materials.AsphaltMaterialId),
                    "ГП-ТРОТ" or "ГП-АЛЛЕЯ" or "3" => (pavingFill, materials.PavingTileMaterialId),
                    "2" => (new Color(205, 155, 105), materials.WoodBenchMaterialId),
                    "ГП-ДЕТИ" or "7" or "12" => (new Color(245, 165, 35), materials.PlaygroundYellowMaterialId),
                    "ГП-СПОРТ" or "14" => (new Color(65, 140, 210), materials.SportsCourtOrangeMaterialId),
                    "ГП-ОТДЫХ" or "10" => (new Color(200, 230, 165), materials.PavingTileMaterialId),
                    "ГП-ТБО" or "6" or "8" => (new Color(145, 140, 135), materials.WasteSiteMaterialId),
                    "ГП-ТП" => (new Color(140, 135, 130), materials.WasteSiteMaterialId),
                    "ГП-ТЕННИС" or "15" => (new Color(245, 215, 130), materials.PavingTileMaterialId),
                    "ГП-КРАСНАЯ" => (new Color(220, 38, 38), materials.RedBoundaryMaterialId),
                    "ГП-БОРД" or "ГП-БОРД-1" or "ГП-БОРД-2" => (curbFill, materials.CurbConcreteMaterialId),
                    "ГП-РАЗМ" => (new Color(252, 252, 252), materials.WhiteStripeMaterialId),
                    "ГП-ДЕРЕВЬЯ" => (new Color(60, 140, 50), materials.TreeFoliageMaterialId),
                    _ => (null, ElementId.InvalidElementId)
                };

                if (fillColor == null)
                {
                    continue;
                }

                try
                {
                    OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                    if (solidPatternId != ElementId.InvalidElementId)
                    {
                        try
                        {
                            ogs.SetSurfaceBackgroundPatternId(solidPatternId);
                            ogs.SetSurfaceBackgroundPatternColor(fillColor);
                            ogs.SetSurfaceBackgroundPatternVisible(true);
                        }
                        catch { }

                        ElementId fgPatId = solidPatternId;
                        Color fgColor = fillColor;
                        if (matId != ElementId.InvalidElementId && doc.GetElement(matId) is Material mat
                            && mat.SurfaceForegroundPatternId != ElementId.InvalidElementId
                            && mat.SurfaceForegroundPatternId != solidPatternId)
                        {
                            fgPatId = mat.SurfaceForegroundPatternId;
                            fgColor = mat.SurfaceForegroundPatternColor;
                        }

                        ogs.SetSurfaceForegroundPatternId(fgPatId);
                        ogs.SetSurfaceForegroundPatternColor(fgColor);
                        ogs.SetSurfaceForegroundPatternVisible(true);
                    }
                    view.SetElementOverrides(elem.Id, ogs);
                }
                catch
                {
                    // Пропускаем, если переопределение заблокировано
                }
            }
        }

        // =========================================================================
        // АНАЛИЗ СТЕН ЗДАНИЯ, КОНТУРЫ И РАСЧЕТ ПЛОЩАДЕЙ (М²)
        // =========================================================================

        private static List<Wall> CollectFirstFloorExteriorWalls(Document doc, Level firstLevel)
        {
            List<Wall> allWalls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .OfType<Wall>()
                .ToList();

            List<Wall> exteriorFirstFloor = allWalls
                .Where(w => w.WallType?.Function == WallFunction.Exterior
                            && (w.LevelId == firstLevel.Id || IsWallAtLevelElevation(doc, w, firstLevel.Elevation)))
                .ToList();

            if (exteriorFirstFloor.Count > 0)
            {
                return exteriorFirstFloor;
            }

            List<Wall> allExterior = allWalls
                .Where(w => w.WallType?.Function == WallFunction.Exterior)
                .ToList();

            if (allExterior.Count > 0)
            {
                return allExterior;
            }

            List<Wall> firstFloorWalls = allWalls
                .Where(w => w.LevelId == firstLevel.Id || IsWallAtLevelElevation(doc, w, firstLevel.Elevation))
                .ToList();

            return firstFloorWalls.Count > 0 ? firstFloorWalls : allWalls;
        }

        private static bool IsWallAtLevelElevation(Document doc, Wall wall, double targetElevation)
        {
            if (doc.GetElement(wall.LevelId) is Level wallLevel)
            {
                return Math.Abs(wallLevel.Elevation - targetElevation) < 2.0;
            }
            return false;
        }

        private static BoundingBoxXYZ ComputeBuildingBoundingBox(List<Wall> walls, double baseZ)
        {
            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;
            double maxZ = baseZ + (6000.0 * MmToFeet);

            foreach (Wall wall in walls)
            {
                BoundingBoxXYZ? wBox = wall.get_BoundingBox(null);
                if (wBox != null)
                {
                    minX = Math.Min(minX, wBox.Min.X);
                    maxX = Math.Max(maxX, wBox.Max.X);
                    minY = Math.Min(minY, wBox.Min.Y);
                    maxY = Math.Max(maxY, wBox.Max.Y);
                    maxZ = Math.Max(maxZ, wBox.Max.Z);
                }
                else if (wall.Location is LocationCurve { Curve: { } curve })
                {
                    for (int i = 0; i <= 1; i++)
                    {
                        XYZ pt = curve.GetEndPoint(i);
                        minX = Math.Min(minX, pt.X);
                        maxX = Math.Max(maxX, pt.X);
                        minY = Math.Min(minY, pt.Y);
                        maxY = Math.Max(maxY, pt.Y);
                    }
                }
            }

            if (minX >= maxX || minY >= maxY)
            {
                double half = 6000.0 * MmToFeet;
                minX = -half;
                maxX = half;
                minY = -half;
                maxY = half;
            }

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, baseZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private static CurveLoop BuildClosedBuildingFootprint(
            List<Wall> exteriorWalls,
            BoundingBoxXYZ buildingBox,
            double planeZ)
        {
            return CreateRectangleLoop(
                buildingBox.Min.X,
                buildingBox.Min.Y,
                buildingBox.Max.X,
                buildingBox.Max.Y,
                planeZ);
        }

        private static List<XYZ> GetLoopVertices(CurveLoop loop)
        {
            return loop
                .SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1) })
                .ToList();
        }

        private static CurveLoop ShiftLoopZ(CurveLoop source, double targetZ)
        {
            Curve? first = source.FirstOrDefault();
            if (first == null) return new CurveLoop();
            double currentZ = first.GetEndPoint(0).Z;
            double dz = targetZ - currentZ;
            if (Math.Abs(dz) < 1e-6) return source;

            try
            {
                return CurveLoop.CreateViaTransform(source, Transform.CreateTranslation(new XYZ(0, 0, dz)));
            }
            catch
            {
                CurveLoop result = new CurveLoop();
                foreach (Curve c in source)
                {
                    XYZ p0 = c.GetEndPoint(0);
                    XYZ p1 = c.GetEndPoint(1);
                    result.Append(Line.CreateBound(new XYZ(p0.X, p0.Y, targetZ), new XYZ(p1.X, p1.Y, targetZ)));
                }
                return result;
            }
        }

        private static CurveLoop CreateRectangleLoop(double minX, double minY, double maxX, double maxY, double z)
        {
            XYZ p1 = new XYZ(minX, minY, z);
            XYZ p2 = new XYZ(maxX, minY, z);
            XYZ p3 = new XYZ(maxX, maxY, z);
            XYZ p4 = new XYZ(minX, maxY, z);

            CurveLoop loop = new CurveLoop();
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p4));
            loop.Append(Line.CreateBound(p4, p1));
            return loop;
        }

        private static CurveLoop CreatePolygonLoop(double centerX, double centerY, double radiusFt, int sides, double z)
        {
            List<XYZ> pts = new List<XYZ>(sides);
            for (int i = 0; i < sides; i++)
            {
                double angle = (2.0 * Math.PI * i) / sides;
                pts.Add(new XYZ(
                    centerX + (radiusFt * Math.Cos(angle)),
                    centerY + (radiusFt * Math.Sin(angle)),
                    z));
            }

            CurveLoop loop = new CurveLoop();
            for (int i = 0; i < sides; i++)
            {
                loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % sides]));
            }
            return loop;
        }

        private static CurveLoop CreateOctagonLoop(double centerX, double centerY, double radiusFt, double z)
        {
            return CreatePolygonLoop(centerX, centerY, radiusFt, 8, z);
        }

        // =========================================================================
        // ЭЛЕМЕНТЫ ГЕНПЛАНА ПО ГОСТ 21.508-2020: КОНТУР УЧАСТКА, ЗОНЫ, МАРКИ, ТАБЛИЦЫ
        // =========================================================================

        private static CurveLoop CreatePolygonLoopFromPoints(List<XYZ> pts)
        {
            CurveLoop loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                XYZ p0 = pts[i];
                XYZ p1 = pts[(i + 1) % pts.Count];
                loop.Append(Line.CreateBound(p0, p1));
            }
            if (!loop.IsCounterclockwise(XYZ.BasisZ))
            {
                loop.Flip();
            }
            return loop;
        }

        /// <summary>
        /// Формирует реалистичный полигональный контур кадастровой границы участка генерального плана (красную линию)
        /// с учетом выбранной геометрической формы (угловой участок, трапеция, многоугольник, прямоугольник).
        /// </summary>
        private static CurveLoop BuildSiteBoundaryLoop(
            BoundingBoxXYZ buildingBox,
            double baseZ,
            SiteBoundaryShape shape,
            SitePlanStyle style,
            Random rng)
        {
            double bMinX = buildingBox.Min.X;
            double bMaxX = buildingBox.Max.X;
            double bMinY = buildingBox.Min.Y;
            double bMaxY = buildingBox.Max.Y;
            double bW = bMaxX - bMinX;
            double bH = bMaxY - bMinY;

            // Щедрые отступы для размещения дорог, парковок и 9 функциональных зон
            double padW = Math.Max(26000.0 * MmToFeet, bW * 0.85);
            double padE = Math.Max(28000.0 * MmToFeet, bW * 0.85);
            double padS = Math.Max(24000.0 * MmToFeet, bH * 0.80);
            double padN = Math.Max(28000.0 * MmToFeet, bH * 0.85);

            double minX = bMinX - padW;
            double maxX = bMaxX + padE;
            double minY = bMinY - padS;
            double maxY = bMaxY + padN;

            List<XYZ> pts = [];

            switch (shape)
            {
                case SiteBoundaryShape.CornerLot:
                    // 📐 Угловой участок со скосом улиц на перекрестках (7 вершин, в точности как на ГОСТ-чертеже)
                    pts.Add(new XYZ(minX, minY + (8000.0 * MmToFeet), baseZ));
                    pts.Add(new XYZ(minX, maxY - (15000.0 * MmToFeet), baseZ));
                    pts.Add(new XYZ(minX + (15000.0 * MmToFeet), maxY, baseZ));
                    pts.Add(new XYZ(maxX - (10000.0 * MmToFeet), maxY, baseZ));
                    pts.Add(new XYZ(maxX, maxY - (6000.0 * MmToFeet), baseZ));
                    pts.Add(new XYZ(maxX, minY, baseZ));
                    pts.Add(new XYZ(minX + (8000.0 * MmToFeet), minY, baseZ));
                    break;

                case SiteBoundaryShape.Trapezoidal:
                    // 📐 Трапециевидный участок со скошенным фасадным фронтом вдоль магистрали (6 вершин)
                    pts.Add(new XYZ(minX, minY + (10000.0 * MmToFeet), baseZ));
                    pts.Add(new XYZ(minX, maxY - (18000.0 * MmToFeet), baseZ));
                    pts.Add(new XYZ(minX + (22000.0 * MmToFeet), maxY, baseZ));
                    pts.Add(new XYZ(maxX, maxY, baseZ));
                    pts.Add(new XYZ(maxX, minY, baseZ));
                    pts.Add(new XYZ(minX + (10000.0 * MmToFeet), minY, baseZ));
                    break;

                case SiteBoundaryShape.Polygonal:
                    // 📐 Сложный многоугольный участок с фасками и уступами (8 вершин)
                    pts.Add(new XYZ(minX, minY + (10000.0 * MmToFeet), baseZ));
                    pts.Add(new XYZ(minX, maxY - (14000.0 * MmToFeet), baseZ));
                    pts.Add(new XYZ(minX + (14000.0 * MmToFeet), maxY, baseZ));
                    pts.Add(new XYZ(maxX - (12000.0 * MmToFeet), maxY, baseZ));
                    pts.Add(new XYZ(maxX, maxY - (8000.0 * MmToFeet), baseZ));
                    pts.Add(new XYZ(maxX, minY + (12000.0 * MmToFeet), baseZ));
                    pts.Add(new XYZ(maxX - (10000.0 * MmToFeet), minY, baseZ));
                    pts.Add(new XYZ(minX + (10000.0 * MmToFeet), minY, baseZ));
                    break;

                default: // Rectangular
                    // 📐 Прямоугольный классический (4 вершины)
                    pts.Add(new XYZ(minX, minY, baseZ));
                    pts.Add(new XYZ(minX, maxY, baseZ));
                    pts.Add(new XYZ(maxX, maxY, baseZ));
                    pts.Add(new XYZ(maxX, minY, baseZ));
                    break;
            }

            return CreatePolygonLoopFromPoints(pts);
        }

        private static void EnsureViewSketchPlane(Document doc, View view)
        {
            if (view.SketchPlane == null)
            {
                try
                {
                    Plane plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    SketchPlane sp = SketchPlane.Create(doc, plane);
                    view.SketchPlane = sp;
                }
                catch
                {
                }
            }
        }

        private static void CreatePlanNumberCircle(
            Document doc,
            ViewPlan sitePlanView,
            ElementId siteCatId,
            XYZ center,
            int number,
            double radiusM,
            double baseZ,
            SiteMaterials materials)
        {
            // Отложенная регистрация марки: отрисовка будет выполнена после выбора итогового масштаба вида на листе
            RegisterPlanMarker(center, number);
        }

        private static void ClearSheetElements(Document doc, ViewSheet sheet)
        {
            try
            {
                List<ElementId> toDelete = [];

                ICollection<ElementId> viewports = sheet.GetAllViewports();
                if (viewports != null && viewports.Count > 0)
                {
                    toDelete.AddRange(viewports);
                }

                List<ElementId> owned = new FilteredElementCollector(doc)
                    .OwnedByView(sheet.Id)
                    .WhereElementIsNotElementType()
                    .Where(e => e is TextNote || e is DetailCurve || e is ScheduleSheetInstance)
                    .Select(e => e.Id)
                    .ToList();
                toDelete.AddRange(owned);

                if (toDelete.Count > 0)
                {
                    doc.Delete(toDelete);
                    doc.Regenerate();
                }
            }
            catch
            {
            }
        }

        private static void ConfigureSitePlanViewDisplay(
            Document doc,
            ViewPlan view,
            CurveLoop buildingLoop,
            double baseZ = 0.0,
            ElementId? whiteMatId = null)
        {
            // 1. Скрываем все внутренние и кровельные категории здания (стены, помещения, двери, окна, полы, крыши, лестницы, мебель и т.д.):
            BuiltInCategory[] catsToHide =
            [
                BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_Rooms,
                BuiltInCategory.OST_RoomTags,
                BuiltInCategory.OST_Doors,
                BuiltInCategory.OST_Windows,
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Roofs,
                BuiltInCategory.OST_Ceilings,
                BuiltInCategory.OST_Stairs,
                BuiltInCategory.OST_Railings,
                BuiltInCategory.OST_Ramps,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_Furniture,
                BuiltInCategory.OST_FurnitureSystems,
                BuiltInCategory.OST_Casework,
                BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_SpecialityEquipment,
                BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_CurtainWallPanels,
                BuiltInCategory.OST_CurtainWallMullions,
                BuiltInCategory.OST_DoorTags,
                BuiltInCategory.OST_WindowTags,
                BuiltInCategory.OST_AreaTags,
                BuiltInCategory.OST_Areas,
                BuiltInCategory.OST_Elev,
                BuiltInCategory.OST_Viewers,
                BuiltInCategory.OST_GenericAnnotation,
                BuiltInCategory.OST_Grids,
                BuiltInCategory.OST_Dimensions
            ];
            foreach (BuiltInCategory cat in catsToHide)
            {
                try
                {
                    ElementId catId = new ElementId(cat);
                    if (view.CanCategoryBeHidden(catId))
                    {
                        view.SetCategoryHidden(catId, true);
                    }
                }
                catch { }
            }

            // 2. Создаем 3D белую плашку контура здания на отметке -65 мм (выше газона и площадок, чтобы в плане ГП-1 контур был 100% белым),
            // и скрываем её на 3D-видах, чтобы в 3D отображалось само здание:
            try
            {
                CurveLoop whiteCapLoop = ShiftLoopZ(buildingLoop, baseZ - (65.0 * MmToFeet));
                Solid whiteCapSolid = GeometryCreationUtilities.CreateExtrusionGeometry(
                    [whiteCapLoop],
                    XYZ.BasisZ,
                    15.0 * MmToFeet,
                    new SolidOptions(whiteMatId ?? ElementId.InvalidElementId, ElementId.InvalidElementId));

                DirectShape dsWhite = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_Site));
                dsWhite.SetShape([whiteCapSolid]);
                SetZoneMetadata(dsWhite, "ГП_2D: Белый контур здания по ГОСТ 21.508", "ГП-ЗДАНИЕ-БЕЛЫЙ");

                foreach (View3D v3d in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate))
                {
                    try { v3d.HideElements([dsWhite.Id]); } catch { }
                }
            }
            catch { }

            // 3. Дополнительно создаем маскирующую белую заливку контура здания (Masking Solid White FilledRegion):
            ElementId whiteRegionTypeId = GetOrCreateWhiteBuildingRegionType(doc);
            if (whiteRegionTypeId != ElementId.InvalidElementId)
            {
                try
                {
                    FilledRegion.Create(doc, whiteRegionTypeId, view.Id, [buildingLoop]);
                }
                catch { }
            }

            // 4. Обводим контур здания четкой черной контурной линией 0.7 мм по ГОСТ 21.508:
            EnsureViewSketchPlane(doc, view);
            GraphicsStyle? wideLineStyle = GetWideLineStyle(doc);
            foreach (Curve curve in buildingLoop)
            {
                try
                {
                    DetailCurve dc = doc.Create.NewDetailCurve(view, curve);
                    if (wideLineStyle != null)
                    {
                        dc.LineStyle = wideLineStyle;
                    }
                }
                catch { }
            }

            // 5. По ГОСТ 21.508 ставим номер здания ("1") в центре
            XYZ center = ComputeLoopCenter(buildingLoop);
            RegisterPlanMarker(center, 1);
        }

        private static ElementId GetOrCreateWhiteBuildingRegionType(Document doc)
        {
            const string typeName = "ГП_Здание_СплошнойБелый_ГОСТ";

            List<FilledRegionType> types = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .ToList();

            ElementId solidId = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f =>
                {
                    FillPattern fp = f.GetFillPattern();
                    return fp.IsSolidFill && fp.Target == FillPatternTarget.Drafting;
                })?.Id ?? ElementId.InvalidElementId;

            FilledRegionType? existing = types.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                try { existing.IsMasking = true; } catch { }
                if (solidId != ElementId.InvalidElementId)
                {
                    try { existing.ForegroundPatternId = solidId; } catch { }
                }
                try { existing.ForegroundPatternColor = new Color(255, 255, 255); } catch { }
                return existing.Id;
            }

            FilledRegionType? baseType = types.FirstOrDefault();
            if (baseType == null)
            {
                return ElementId.InvalidElementId;
            }

            try
            {
                FilledRegionType newType = (FilledRegionType)baseType.Duplicate(typeName);
                try { newType.IsMasking = true; } catch { }
                if (solidId != ElementId.InvalidElementId)
                {
                    try { newType.ForegroundPatternId = solidId; } catch { }
                }
                try { newType.ForegroundPatternColor = new Color(255, 255, 255); } catch { }
                try { newType.BackgroundPatternId = ElementId.InvalidElementId; } catch { }
                return newType.Id;
            }
            catch
            {
                return baseType.Id;
            }
        }

        private static GraphicsStyle? GetWideLineStyle(Document doc)
        {
            try
            {
                Category linesCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                foreach (Category subCat in linesCat.SubCategories)
                {
                    if (subCat.Name.Contains("Широк", StringComparison.OrdinalIgnoreCase) ||
                        subCat.Name.Contains("Основн", StringComparison.OrdinalIgnoreCase) ||
                        subCat.Name.Contains("Wide", StringComparison.OrdinalIgnoreCase) ||
                        subCat.Name.Contains("Земл", StringComparison.OrdinalIgnoreCase))
                    {
                        return subCat.GetGraphicsStyle(GraphicsStyleType.Projection);
                    }
                }
            }
            catch { }
            return null;
        }

        private static XYZ ComputeLoopCenter(CurveLoop loop)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue;
            foreach (Curve c in loop)
            {
                XYZ p = c.GetEndPoint(0);
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
            }
            return new XYZ((minX + maxX) / 2.0, (minY + maxY) / 2.0, minZ);
        }

        private static CurveLoop BuildLoopFromPoints(List<XYZ> pts, double baseZ)
        {
            CurveLoop loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                XYZ p0 = new XYZ(pts[i].X, pts[i].Y, baseZ);
                XYZ p1 = new XYZ(pts[(i + 1) % pts.Count].X, pts[(i + 1) % pts.Count].Y, baseZ);
                if (p0.DistanceTo(p1) > 1e-4)
                {
                    loop.Append(Line.CreateBound(p0, p1));
                }
            }
            if (!loop.IsCounterclockwise(XYZ.BasisZ))
            {
                loop.Flip();
            }
            return loop;
        }

        private static void DrawRedSiteBoundaryWithNodes(
            Document doc,
            ElementId siteCatId,
            CurveLoop siteBoundaryLoop,
            double baseZ,
            SiteMaterials materials)
        {
            List<GeometryObject> boundarySolids = [];
            double ribbonHalfW = 120.0 * MmToFeet;
            double ribbonH = 30.0 * MmToFeet;
            double nodeRadius = 450.0 * MmToFeet;
            double nodeH = 50.0 * MmToFeet;
            double ribbonZ = baseZ - (112.0 * MmToFeet);

            // 1. Красная лента по каждому ребру границы участка
            foreach (Curve curve in siteBoundaryLoop)
            {
                XYZ p0 = curve.GetEndPoint(0);
                XYZ p1 = curve.GetEndPoint(1);
                XYZ delta = p1 - p0;
                double len = delta.GetLength();
                if (len < 1e-4) continue;

                XYZ dir = delta / len;
                XYZ normal = new XYZ(-dir.Y, dir.X, 0.0);

                XYZ a = p0 + (normal * ribbonHalfW);
                XYZ b = p1 + (normal * ribbonHalfW);
                XYZ c = p1 - (normal * ribbonHalfW);
                XYZ d = p0 - (normal * ribbonHalfW);

                CurveLoop segLoop = new CurveLoop();
                segLoop.Append(Line.CreateBound(new XYZ(a.X, a.Y, ribbonZ), new XYZ(b.X, b.Y, ribbonZ)));
                segLoop.Append(Line.CreateBound(new XYZ(b.X, b.Y, ribbonZ), new XYZ(c.X, c.Y, ribbonZ)));
                segLoop.Append(Line.CreateBound(new XYZ(c.X, c.Y, ribbonZ), new XYZ(d.X, d.Y, ribbonZ)));
                segLoop.Append(Line.CreateBound(new XYZ(d.X, d.Y, ribbonZ), new XYZ(a.X, a.Y, ribbonZ)));

                try
                {
                    boundarySolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                        [segLoop], XYZ.BasisZ, ribbonH,
                        new SolidOptions(materials.RedBoundaryMaterialId, ElementId.InvalidElementId)));
                }
                catch
                {
                }
            }

            // 2. Красный круглый маркер-узел на каждом углу границы (как на ГОСТ-чертеже)
            List<XYZ> vertices = siteBoundaryLoop.Select(c => c.GetEndPoint(0)).ToList();
            foreach (XYZ v in vertices)
            {
                CurveLoop nodeLoop = CreatePolygonLoop(v.X, v.Y, nodeRadius, 16, ribbonZ);
                try
                {
                    boundarySolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                        [nodeLoop], XYZ.BasisZ, nodeH,
                        new SolidOptions(materials.RedBoundaryMaterialId, ElementId.InvalidElementId)));
                }
                catch
                {
                }
            }

            if (boundarySolids.Count > 0)
            {
                DirectShape ds = DirectShape.CreateElement(doc, siteCatId);
                ds.SetShape(boundarySolids);
                SetZoneMetadata(ds, "ГП_3D: Красная линия границы участка с узловыми точками (ГОСТ 21.508)", "ГП-КРАСНАЯ");
            }
        }

        // =========================================================================
        // 3D-ГЕНЕРАТОРЫ МАФ: СТОЛИКИ РЕСТОРАНА, ФОНТАН, ДЕБАРКАДЕР, ТБО, ТП
        // =========================================================================

        private static List<GeometryObject> CreateDiningTableWithChairsSolid(XYZ center, SiteMaterials materials)
        {
            List<GeometryObject> solids = [];
            double tHalf = 550.0 * MmToFeet;
            double tH = 750.0 * MmToFeet;
            double topThk = 40.0 * MmToFeet;
            double legR = 50.0 * MmToFeet;

            CurveLoop tableTop = CreateRectangleLoop(center.X - tHalf, center.Y - tHalf, center.X + tHalf, center.Y + tHalf, center.Z + tH - topThk);
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([tableTop], XYZ.BasisZ, topThk,
                new SolidOptions(materials.WoodBenchMaterialId, ElementId.InvalidElementId)));

            CurveLoop legLoop = CreateOctagonLoop(center.X, center.Y, legR, center.Z);
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([legLoop], XYZ.BasisZ, tH - topThk,
                new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));
            CurveLoop baseLoop = CreateOctagonLoop(center.X, center.Y, legR * 3.5, center.Z);
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([baseLoop], XYZ.BasisZ, 25.0 * MmToFeet,
                new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));

            double chairOffset = 800.0 * MmToFeet;
            double cHalf = 220.0 * MmToFeet;
            double seatZ = center.Z + (440.0 * MmToFeet);
            double backH = 400.0 * MmToFeet;

            XYZ[] chairCenters = [new XYZ(center.X - chairOffset, center.Y, center.Z), new XYZ(center.X + chairOffset, center.Y, center.Z)];
            foreach (XYZ cc in chairCenters)
            {
                CurveLoop seat = CreateRectangleLoop(cc.X - cHalf, cc.Y - cHalf, cc.X + cHalf, cc.Y + cHalf, seatZ);
                solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([seat], XYZ.BasisZ, 35.0 * MmToFeet,
                    new SolidOptions(materials.WoodBenchMaterialId, ElementId.InvalidElementId)));

                double backX = cc.X > center.X ? cc.X + cHalf - (30.0 * MmToFeet) : cc.X - cHalf;
                CurveLoop back = CreateRectangleLoop(backX, cc.Y - cHalf, backX + (30.0 * MmToFeet), cc.Y + cHalf, seatZ + (35.0 * MmToFeet));
                solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([back], XYZ.BasisZ, backH,
                    new SolidOptions(materials.WoodBenchMaterialId, ElementId.InvalidElementId)));

                CurveLoop chairLeg = CreateOctagonLoop(cc.X, cc.Y, 40.0 * MmToFeet, center.Z);
                solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([chairLeg], XYZ.BasisZ, 440.0 * MmToFeet,
                    new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));
            }

            return solids;
        }

        private static List<GeometryObject> CreateTerracePlantersSolid(
            double minX, double minY, double maxX, double maxY, double baseZ, SiteMaterials materials)
        {
            List<GeometryObject> solids = [];
            double boxW = 400.0 * MmToFeet;
            double boxH = 550.0 * MmToFeet;
            double shrubH = 400.0 * MmToFeet;

            for (double x = minX + (1000.0 * MmToFeet); x <= maxX - (2200.0 * MmToFeet); x += 2600.0 * MmToFeet)
            {
                double x1 = x + (1800.0 * MmToFeet);
                CurveLoop box = CreateRectangleLoop(x, minY, x1, minY + boxW, baseZ);
                solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([box], XYZ.BasisZ, boxH,
                    new SolidOptions(materials.WoodBenchMaterialId, ElementId.InvalidElementId)));

                CurveLoop shrub = CreateRectangleLoop(x + (40.0 * MmToFeet), minY + (40.0 * MmToFeet),
                    x1 - (40.0 * MmToFeet), minY + boxW - (40.0 * MmToFeet), baseZ + boxH);
                solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([shrub], XYZ.BasisZ, shrubH,
                    new SolidOptions(materials.ShrubFoliageMaterialId, ElementId.InvalidElementId)));
            }

            return solids;
        }

        private static List<GeometryObject> CreateFountainSolid(XYZ center, double radiusFt, SiteMaterials materials)
        {
            List<GeometryObject> solids = [];
            double basinH = 500.0 * MmToFeet;
            double wallThk = 300.0 * MmToFeet;

            CurveLoop outLoop = CreatePolygonLoop(center.X, center.Y, radiusFt, 24, center.Z);
            CurveLoop inLoop = CreatePolygonLoop(center.X, center.Y, radiusFt - wallThk, 24, center.Z);
            inLoop.Flip();
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([outLoop, inLoop], XYZ.BasisZ, basinH,
                new SolidOptions(materials.CurbConcreteMaterialId, ElementId.InvalidElementId)));

            CurveLoop waterLoop = CreatePolygonLoop(center.X, center.Y, radiusFt - wallThk, 24, center.Z);
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([waterLoop], XYZ.BasisZ, basinH - (80.0 * MmToFeet),
                new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));

            CurveLoop colLoop = CreatePolygonLoop(center.X, center.Y, 350.0 * MmToFeet, 16, center.Z);
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([colLoop], XYZ.BasisZ, 1200.0 * MmToFeet,
                new SolidOptions(materials.CurbConcreteMaterialId, ElementId.InvalidElementId)));

            CurveLoop upperBowl = CreatePolygonLoop(center.X, center.Y, 700.0 * MmToFeet, 16, center.Z + (1200.0 * MmToFeet));
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([upperBowl], XYZ.BasisZ, 250.0 * MmToFeet,
                new SolidOptions(materials.CurbConcreteMaterialId, ElementId.InvalidElementId)));

            return solids;
        }

        private static List<GeometryObject> CreateLoadingDockRampSolid(
            double minX, double minY, double maxX, double maxY, double baseZ, SiteMaterials materials)
        {
            List<GeometryObject> solids = [];
            double dockH = 1100.0 * MmToFeet;

            CurveLoop dockBody = CreateRectangleLoop(minX + (1200.0 * MmToFeet), minY + (1200.0 * MmToFeet),
                maxX - (1200.0 * MmToFeet), maxY - (1200.0 * MmToFeet), baseZ);
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([dockBody], XYZ.BasisZ, dockH,
                new SolidOptions(materials.CurbConcreteMaterialId, ElementId.InvalidElementId)));

            double canopyH = 3400.0 * MmToFeet;
            double postR = 50.0 * MmToFeet;
            XYZ[] posts =
            [
                new XYZ(minX + (1200.0 * MmToFeet), minY + (1200.0 * MmToFeet), baseZ),
                new XYZ(maxX - (1200.0 * MmToFeet), minY + (1200.0 * MmToFeet), baseZ),
                new XYZ(minX + (1200.0 * MmToFeet), maxY - (1200.0 * MmToFeet), baseZ),
                new XYZ(maxX - (1200.0 * MmToFeet), maxY - (1200.0 * MmToFeet), baseZ)
            ];
            foreach (XYZ p in posts)
            {
                CurveLoop postLoop = CreateOctagonLoop(p.X, p.Y, postR, p.Z);
                solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([postLoop], XYZ.BasisZ, canopyH,
                    new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));
            }

            CurveLoop roofLoop = CreateRectangleLoop(minX + (800.0 * MmToFeet), minY + (800.0 * MmToFeet),
                maxX - (800.0 * MmToFeet), maxY - (800.0 * MmToFeet), baseZ + canopyH);
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([roofLoop], XYZ.BasisZ, 120.0 * MmToFeet,
                new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));

            return solids;
        }

        private static List<GeometryObject> CreateWasteEnclosureAndBins(
            double minX, double minY, double maxX, double maxY, double baseZ, SiteMaterials materials)
        {
            List<GeometryObject> solids = [];
            double encH = 1500.0 * MmToFeet;
            double wallThk = 120.0 * MmToFeet;

            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [CreateRectangleLoop(minX, maxY - wallThk, maxX, maxY, baseZ)],
                XYZ.BasisZ, encH, new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [CreateRectangleLoop(minX, minY, minX + wallThk, maxY, baseZ)],
                XYZ.BasisZ, encH, new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [CreateRectangleLoop(minX, minY, maxX, minY + wallThk, baseZ)],
                XYZ.BasisZ, encH, new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));

            double binW = 900.0 * MmToFeet;
            double binH = 1100.0 * MmToFeet;
            double binGap = 350.0 * MmToFeet;
            double binStartX = minX + (600.0 * MmToFeet);
            double binY = (minY + maxY - binW) / 2.0;

            for (int b = 0; b < 3; b++)
            {
                double bx = binStartX + (b * (binW + binGap));
                CurveLoop binBody = CreateRectangleLoop(bx, binY, bx + binW, binY + binW, baseZ);
                solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    [binBody], XYZ.BasisZ, binH,
                    new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));

                CurveLoop binLid = CreateRectangleLoop(bx - (30.0 * MmToFeet), binY - (30.0 * MmToFeet),
                    bx + binW + (30.0 * MmToFeet), binY + binW + (30.0 * MmToFeet), baseZ + binH);
                solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    [binLid], XYZ.BasisZ, 70.0 * MmToFeet,
                    new SolidOptions(materials.YellowStopperMaterialId, ElementId.InvalidElementId)));
            }

            return solids;
        }

        private static List<GeometryObject> CreateSubstationBuildingSolid(
            double minX, double minY, double maxX, double maxY, double baseZ, SiteMaterials materials)
        {
            List<GeometryObject> solids = [];
            CurveLoop subBody = CreateRectangleLoop(minX, minY, maxX, maxY, baseZ);
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [subBody], XYZ.BasisZ, 2800.0 * MmToFeet,
                new SolidOptions(materials.CurbConcreteMaterialId, ElementId.InvalidElementId)));

            CurveLoop subRoof = CreateRectangleLoop(minX - (200.0 * MmToFeet), minY - (200.0 * MmToFeet),
                maxX + (200.0 * MmToFeet), maxY + (200.0 * MmToFeet), baseZ + (2800.0 * MmToFeet));
            solids.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                [subRoof], XYZ.BasisZ, 160.0 * MmToFeet,
                new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));

            return solids;
        }

        private static SitePlanExplicationData CreateGostFunctionalZonesAndMarkers(
            Document doc,
            ViewPlan sitePlanView,
            ElementId siteCatId,
            ElementId plantingCatId,
            BoundingBoxXYZ buildingBox,
            double baseZ,
            CurveLoop buildingLoop,
            CurveLoop siteBoundaryLoop,
            double rMinX, double rMaxX, double rMinY, double rMaxY,
            double sMinX, double sMaxX, double sMinY, double sMaxY,
            double buildingAreaM2,
            double blindAreaM2,
            double roadAreaM2,
            int parkingSpotsCount,
            double parkingAreaM2,
            CurveLoop parkingLoop,
            SitePlanStyle effectiveStyle,
            BuildingPurposeCategory purposeCategory,
            string buildingPurposeName,
            SiteMaterials materials,
            Random rng,
            SitePlanEditorConfig? editorConfig = null)
        {
            double bMinX = buildingBox.Min.X;
            double bMaxX = buildingBox.Max.X;
            double bMinY = buildingBox.Min.Y;
            double bMaxY = buildingBox.Max.Y;
            double bWidth = bMaxX - bMinX;
            double bCenterX = (bMinX + bMaxX) / 2.0;
            double bCenterY = (bMinY + bMaxY) / 2.0;
            double curbW = CurbWidthMm * MmToFeet;
            double totalSiteAreaM2 = CalculateCurveLoopAreaM2(siteBoundaryLoop);

            // 1. Красная линия границы участка с круглыми узловыми точками (ГОСТ 21.508)
            DrawRedSiteBoundaryWithNodes(doc, siteCatId, siteBoundaryLoop, baseZ, materials);

            if (purposeCategory == BuildingPurposeCategory.Restaurant)
            {
                // =========================================================================
                // А. СПЕЦИАЛИЗИРОВАННЫЙ ГЕНПЛАН РЕСТОРАНА / КАФЕ / ОБЩЕПИТА
                // =========================================================================
                bool incTerrace = editorConfig?.IncludeTerrace ?? true;
                bool incPlaza = editorConfig?.IncludeFountainPlaza ?? true;
                bool incGuestParking = editorConfig?.IncludeGuestParking ?? true;
                bool incDock = editorConfig?.IncludeServiceDock ?? true;
                bool incWaste = editorConfig?.IncludeWasteSite ?? true;
                bool incKids = editorConfig?.IncludePlayground ?? true;
                bool incTP = editorConfig?.IncludeUtilityBlock ?? true;
                bool incStaffParking = editorConfig?.IncludeStaffParking ?? true;

                int curMark = 1;
                List<string[]> t1 = [];

                // № 1: Здание ресторана
                XYZ buildingCenter = new XYZ(bCenterX, bCenterY, baseZ + (2000.0 * MmToFeet));
                CreatePlanNumberCircle(doc, sitePlanView, siteCatId, buildingCenter, curMark, 1.6, baseZ, materials);
                t1.Add([curMark.ToString(), $"Проектируемое здание ({buildingPurposeName})", $"Застройка ({buildingAreaM2:F1} м²)"]);
                curMark++;

                // № 2: Открытая летняя веранда (терраса) ресторана со столиками и кашпо
                double terraceAreaM2 = 0.0;
                if (incTerrace)
                {
                    double terrW = Math.Clamp(bWidth * 0.75, 8000.0 * MmToFeet, 16000.0 * MmToFeet);
                    double terrH = 6500.0 * MmToFeet;
                    double terrMinX = bCenterX - (terrW / 2.0);
                    double terrMaxX = terrMinX + terrW;
                    double terrMinY = bMinY - (terrH + (1200.0 * MmToFeet));
                    double terrMaxY = bMinY - (600.0 * MmToFeet);

                    CurveLoop terrLoop = CreateRectangleLoop(terrMinX, terrMinY, terrMaxX, terrMaxY, baseZ - (20.0 * MmToFeet));
                    Create3DExtrusionDirectShape(doc, siteCatId, [terrLoop], 120.0 * MmToFeet, materials.WoodBenchMaterialId,
                        "ГП_3D: Летняя веранда ресторана (деревянный настил)", "2");

                    CurveLoop terrCurbOut = CreateRectangleLoop(terrMinX - curbW, terrMinY - curbW, terrMaxX + curbW, terrMaxY + curbW, baseZ - (80.0 * MmToFeet));
                    CurveLoop terrCurbIn = CreateRectangleLoop(terrMinX, terrMinY, terrMaxX, terrMaxY, baseZ - (80.0 * MmToFeet));
                    terrCurbIn.Flip();
                    Create3DExtrusionDirectShape(doc, siteCatId, [terrCurbOut, terrCurbIn], 180.0 * MmToFeet, materials.CurbConcreteMaterialId,
                        "ГП_3D: Бордюр веранды", "ГП-БОРД");

                    List<GeometryObject> diningSolids = [];
                    double tblSpacing = 3200.0 * MmToFeet;
                    for (double tx = terrMinX + (1800.0 * MmToFeet); tx <= terrMaxX - (1800.0 * MmToFeet); tx += tblSpacing)
                    {
                        for (double ty = terrMinY + (1600.0 * MmToFeet); ty <= terrMaxY - (1600.0 * MmToFeet); ty += tblSpacing)
                        {
                            diningSolids.AddRange(CreateDiningTableWithChairsSolid(new XYZ(tx, ty, baseZ + (100.0 * MmToFeet)), materials));
                        }
                    }
                    diningSolids.AddRange(CreateTerracePlantersSolid(terrMinX, terrMinY, terrMaxX, terrMaxY, baseZ + (100.0 * MmToFeet), materials));

                    if (diningSolids.Count > 0)
                    {
                        DirectShape diningDs = DirectShape.CreateElement(doc, siteCatId);
                        diningDs.SetShape(diningSolids);
                        SetZoneMetadata(diningDs, "ГП_3D: Столики и кашпо летней веранды ресторана", "ГП-МАФ");
                    }

                    XYZ terrCenter = new XYZ((terrMinX + terrMaxX) / 2.0, (terrMinY + terrMaxY) / 2.0, baseZ);
                    CreatePlanNumberCircle(doc, sitePlanView, siteCatId, terrCenter, curMark, 1.4, baseZ, materials);
                    terraceAreaM2 = Math.Round((terrMaxX - terrMinX) * (terrMaxY - terrMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);
                    t1.Add([curMark.ToString(), "Открытая летняя веранда (терраса)", $"Деревянный настил, столики ({terraceAreaM2:F1} м²)"]);
                    curMark++;
                }

                // № 3: Парадная входная группа со сквером и круглым фонтаном
                double plazaAreaM2 = 0.0;
                if (incPlaza)
                {
                    double sqW = 12000.0 * MmToFeet;
                    double sqH = 9000.0 * MmToFeet;
                    double sqMinX = Math.Min(sMaxX - sqW - (2000.0 * MmToFeet), rMaxX + (1500.0 * MmToFeet));
                    double sqMaxX = sqMinX + sqW;
                    double sqMinY = Math.Max(sMinY + (2000.0 * MmToFeet), rMinY - (sqH / 2.0));
                    double sqMaxY = sqMinY + sqH;

                    CurveLoop sqLoop = CreateRectangleLoop(sqMinX, sqMinY, sqMaxX, sqMaxY, baseZ - (20.0 * MmToFeet));
                    Create3DExtrusionDirectShape(doc, siteCatId, [sqLoop], 100.0 * MmToFeet, materials.PavingTileMaterialId,
                        "ГП_3D: Парадный сквер гостей ресторана", "3");

                    XYZ fountainPt = new XYZ((sqMinX + sqMaxX) / 2.0, (sqMinY + sqMaxY) / 2.0, baseZ);
                    List<GeometryObject> fountainSolids = CreateFountainSolid(fountainPt, 2200.0 * MmToFeet, materials);
                    if (fountainSolids.Count > 0)
                    {
                        DirectShape ftDs = DirectShape.CreateElement(doc, siteCatId);
                        ftDs.SetShape(fountainSolids);
                        SetZoneMetadata(ftDs, "ГП_3D: Декоративный фонтан сквера ресторана", "ГП-МАФ");
                    }

                    CreatePlanNumberCircle(doc, sitePlanView, siteCatId, fountainPt, curMark, 1.4, baseZ, materials);
                    plazaAreaM2 = Math.Round((sqMaxX - sqMinX) * (sqMaxY - sqMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);
                    t1.Add([curMark.ToString(), "Парадная входная группа со сквером", $"Фонтан, цветники, скамьи ({plazaAreaM2:F1} м²)"]);
                    curMark++;
                }

                // № 4: Гостевая автостоянка для посетителей ресторана
                double guestParkingAreaM2 = 0.0;
                if (incGuestParking)
                {
                    List<XYZ> pVerts = GetLoopVertices(parkingLoop);
                    XYZ parkCenter = new XYZ(
                        (pVerts.Min(p => p.X) + pVerts.Max(p => p.X)) / 2.0,
                        (pVerts.Min(p => p.Y) + pVerts.Max(p => p.Y)) / 2.0,
                        baseZ);
                    CreatePlanNumberCircle(doc, sitePlanView, siteCatId, parkCenter, curMark, 1.4, baseZ, materials);
                    guestParkingAreaM2 = parkingAreaM2;
                    t1.Add([curMark.ToString(), "Гостевая автостоянка для посетителей", $"Асфальтобетон, {parkingSpotsCount} м/м ({guestParkingAreaM2:F1} м²)"]);
                    curMark++;
                }

                // № 5: Хозяйственный двор и дебаркадер разгрузки кухни
                double dockAreaM2 = 0.0;
                double dockMinX = Math.Max(sMinX + (2000.0 * MmToFeet), rMinX - (8500.0 * MmToFeet) - (1500.0 * MmToFeet));
                double dockMaxX = dockMinX + (8500.0 * MmToFeet);
                double dockMinY = Math.Min(sMaxY - (6500.0 * MmToFeet) - (2000.0 * MmToFeet), (rMinY + rMaxY) / 2.0);
                double dockMaxY = dockMinY + (6500.0 * MmToFeet);
                if (incDock)
                {
                    CurveLoop dockLoop = CreateRectangleLoop(dockMinX, dockMinY, dockMaxX, dockMaxY, baseZ - (20.0 * MmToFeet));
                    Create3DExtrusionDirectShape(doc, siteCatId, [dockLoop], 100.0 * MmToFeet, materials.AsphaltMaterialId,
                        "ГП_3D: Хоздвор кухни ресторана (дебаркадер)", "5");

                    List<GeometryObject> dockSolids = CreateLoadingDockRampSolid(dockMinX, dockMinY, dockMaxX, dockMaxY, baseZ, materials);
                    if (dockSolids.Count > 0)
                    {
                        DirectShape dockDs = DirectShape.CreateElement(doc, siteCatId);
                        dockDs.SetShape(dockSolids);
                        SetZoneMetadata(dockDs, "ГП_3D: Разгрузочная рампа и навес кухни", "ГП-МАФ");
                    }

                    XYZ dockCenter = new XYZ((dockMinX + dockMaxX) / 2.0, (dockMinY + dockMaxY) / 2.0, baseZ);
                    CreatePlanNumberCircle(doc, sitePlanView, siteCatId, dockCenter, curMark, 1.3, baseZ, materials);
                    dockAreaM2 = Math.Round((dockMaxX - dockMinX) * (dockMaxY - dockMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);
                    t1.Add([curMark.ToString(), "Хозяйственный двор и дебаркадер кухни", $"Приемка продуктов ({dockAreaM2:F1} м²)"]);
                    curMark++;
                }

                // № 6: Площадка для сбора ТБО и отходов кухни (ограждение + 3 бака)
                double wasteAreaM2 = 0.0;
                double wasteMinX = dockMinX;
                double wasteMaxX = wasteMinX + (5500.0 * MmToFeet);
                double wasteMinY = dockMinY - (3000.0 * MmToFeet) - (1500.0 * MmToFeet);
                double wasteMaxY = wasteMinY + (3000.0 * MmToFeet);
                int seats = Math.Max(50, (int)Math.Round(buildingAreaM2 / 4.5));
                double dailyWasteM3 = (seats * 1.45 * 1.20) / 365.0;
                int wasteBins = Math.Max(2, (int)Math.Ceiling(dailyWasteM3 / (0.9 * 0.75)));
                if (incWaste)
                {
                    CurveLoop wasteLoop = CreateRectangleLoop(wasteMinX, wasteMinY, wasteMaxX, wasteMaxY, baseZ - (20.0 * MmToFeet));
                    Create3DExtrusionDirectShape(doc, siteCatId, [wasteLoop], 100.0 * MmToFeet, materials.WasteSiteMaterialId,
                        "ГП_3D: Площадка ТБО ресторана", "6");

                    List<GeometryObject> wasteSolids = CreateWasteEnclosureAndBins(wasteMinX, wasteMinY, wasteMaxX, wasteMaxY, baseZ, materials);
                    if (wasteSolids.Count > 0)
                    {
                        DirectShape wDs = DirectShape.CreateElement(doc, siteCatId);
                        wDs.SetShape(wasteSolids);
                        SetZoneMetadata(wDs, "ГП_3D: Контейнеры ТБО ресторана", "ГП-МАФ");
                    }

                    XYZ wasteCenter = new XYZ((wasteMinX + wasteMaxX) / 2.0, (wasteMinY + wasteMaxY) / 2.0, baseZ);
                    CreatePlanNumberCircle(doc, sitePlanView, siteCatId, wasteCenter, curMark, 1.3, baseZ, materials);
                    wasteAreaM2 = Math.Round((wasteMaxX - wasteMinX) * (wasteMaxY - wasteMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);
                    t1.Add([curMark.ToString(), "Площадка ТБО и раздельного сбора отходов", $"Огражденная, {wasteBins} конт. ({wasteAreaM2:F1} м²)"]);
                    curMark++;
                }

                // № 7: Детский семейный игровой уголок ресторана
                double kidsPlayAreaM2 = 0.0;
                double terrMaxXVal = bCenterX + (Math.Clamp(bWidth * 0.75, 8000.0 * MmToFeet, 16000.0 * MmToFeet) / 2.0);
                double terrMinYVal = bMinY - (6500.0 * MmToFeet + (1200.0 * MmToFeet));
                double kidsW = 7500.0 * MmToFeet;
                double kidsH = 6000.0 * MmToFeet;
                double kidsMinX = Math.Min(sMaxX - kidsW - (2000.0 * MmToFeet), terrMaxXVal + (2000.0 * MmToFeet));
                double kidsMaxX = kidsMinX + kidsW;
                double kidsMinY = terrMinYVal;
                double kidsMaxY = kidsMinY + kidsH;
                if (incKids)
                {
                    CurveLoop kidsLoop = CreateRectangleLoop(kidsMinX, kidsMinY, kidsMaxX, kidsMaxY, baseZ - (20.0 * MmToFeet));
                    Create3DExtrusionDirectShape(doc, siteCatId, [kidsLoop], 100.0 * MmToFeet, materials.PlaygroundYellowMaterialId,
                        "ГП_3D: Детский уголок ресторана", "7");

                    List<GeometryObject> kidsMaf = [];
                    CurveLoop sandbox = CreateRectangleLoop(kidsMinX + (800.0 * MmToFeet), kidsMinY + (800.0 * MmToFeet),
                        kidsMinX + (3000.0 * MmToFeet), kidsMinY + (3000.0 * MmToFeet), baseZ);
                    kidsMaf.Add(GeometryCreationUtilities.CreateExtrusionGeometry([sandbox], XYZ.BasisZ, 250.0 * MmToFeet,
                        new SolidOptions(materials.WoodBenchMaterialId, ElementId.InvalidElementId)));
                    kidsMaf.AddRange(CreateBenchSolid(new XYZ(kidsMaxX - (800.0 * MmToFeet), (kidsMinY + kidsMaxY) / 2.0, baseZ), Math.PI / 2.0, materials));
                    if (kidsMaf.Count > 0)
                    {
                        DirectShape kDs = DirectShape.CreateElement(doc, siteCatId);
                        kDs.SetShape(kidsMaf);
                        SetZoneMetadata(kDs, "ГП_3D: Песочница и скамейка детского уголка", "ГП-МАФ");
                    }

                    XYZ kidsCenter = new XYZ((kidsMinX + kidsMaxX) / 2.0, (kidsMinY + kidsMaxY) / 2.0, baseZ);
                    CreatePlanNumberCircle(doc, sitePlanView, siteCatId, kidsCenter, curMark, 1.3, baseZ, materials);
                    kidsPlayAreaM2 = Math.Round((kidsMaxX - kidsMinX) * (kidsMaxY - kidsMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);
                    t1.Add([curMark.ToString(), "Детский семейный игровой уголок", $"Песчано-полимерное покр. ({kidsPlayAreaM2:F1} м²)"]);
                    curMark++;
                }

                // № 8: Инженерный блок (ТП / хладоцентр / вентиляция кухни)
                double utilityAreaM2 = 0.0;
                double subW = 4500.0 * MmToFeet;
                double subH = 3500.0 * MmToFeet;
                double subMinX = wasteMaxX + (1500.0 * MmToFeet);
                double subMaxX = subMinX + subW;
                double subMinY = wasteMinY;
                double subMaxY = wasteMinY + subH;
                if (incTP)
                {
                    List<GeometryObject> subSolids = CreateSubstationBuildingSolid(subMinX, subMinY, subMaxX, subMaxY, baseZ, materials);
                    if (subSolids.Count > 0)
                    {
                        DirectShape subDs = DirectShape.CreateElement(doc, siteCatId);
                        subDs.SetShape(subSolids);
                        SetZoneMetadata(subDs, "ГП_3D: Хладоцентр и ТП ресторана", "8");
                    }

                    XYZ subCenter = new XYZ((subMinX + subMaxX) / 2.0, (subMinY + subMaxY) / 2.0, baseZ + (3000.0 * MmToFeet));
                    CreatePlanNumberCircle(doc, sitePlanView, siteCatId, subCenter, curMark, 1.3, baseZ, materials);
                    utilityAreaM2 = Math.Round((subMaxX - subMinX) * (subMaxY - subMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);
                    t1.Add([curMark.ToString(), "Инженерный блок (ТП / хладоцентр)", $"Электроснабжение, вентиляция ({utilityAreaM2:F1} м²)"]);
                    curMark++;
                }

                // № 9: Служебная автостоянка персонала ресторана
                double staffAreaM2 = 0.0;
                int staffSpots = 0;
                if (incStaffParking)
                {
                    staffSpots = 4;
                    double staffW = 7500.0 * MmToFeet;
                    double staffH = 5500.0 * MmToFeet;
                    double staffMinX = subMaxX + (1500.0 * MmToFeet);
                    double staffMaxX = staffMinX + staffW;
                    double staffMinY = wasteMinY;
                    double staffMaxY = staffMinY + staffH;

                    CurveLoop staffLoop = CreateRectangleLoop(staffMinX, staffMinY, staffMaxX, staffMaxY, baseZ - (20.0 * MmToFeet));
                    Create3DExtrusionDirectShape(doc, siteCatId, [staffLoop], 100.0 * MmToFeet, materials.AsphaltMaterialId,
                        "ГП_3D: Служебная парковка персонала ресторана", "9");

                    XYZ staffCenter = new XYZ((staffMinX + staffMaxX) / 2.0, (staffMinY + staffMaxY) / 2.0, baseZ);
                    CreatePlanNumberCircle(doc, sitePlanView, siteCatId, staffCenter, curMark, 1.3, baseZ, materials);
                    staffAreaM2 = Math.Round((staffMaxX - staffMinX) * (staffMaxY - staffMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);
                    t1.Add([curMark.ToString(), "Служебная автостоянка персонала", $"Охраняемая площадка, {staffSpots} м/м ({staffAreaM2:F1} м²)"]);
                    curMark++;
                }

                // Баланс территории
                double sumConstructed = buildingAreaM2 + blindAreaM2 + roadAreaM2 + guestParkingAreaM2 +
                                        terraceAreaM2 + plazaAreaM2 + dockAreaM2 + wasteAreaM2 +
                                        kidsPlayAreaM2 + utilityAreaM2 + staffAreaM2;
                double actualGreenM2 = Math.Max(0.0, totalSiteAreaM2 - sumConstructed);

                int t2Num = 1;
                List<string[]> t2 = [];
                if (incTerrace && terraceAreaM2 > 0.01)
                {
                    t2.Add([$"{t2Num++}. Открытая летняя веранда", "0.5", seats.ToString(), $"{seats * 0.5:F1}", $"{terraceAreaM2:F1}"]);
                }
                if (incPlaza && plazaAreaM2 > 0.01)
                {
                    t2.Add([$"{t2Num++}. Входной сквер с фонтаном", "0.8", seats.ToString(), $"{seats * 0.8:F1}", $"{plazaAreaM2:F1}"]);
                }
                if (incGuestParking && guestParkingAreaM2 > 0.01)
                {
                    t2.Add([$"{t2Num++}. Гостевые парковки", "1.2", seats.ToString(), $"{seats * 1.2:F1}", $"{guestParkingAreaM2:F1}"]);
                }
                if (incDock && dockAreaM2 > 0.01)
                {
                    t2.Add([$"{t2Num++}. Хоздвор и дебаркадер кухни", "0.4", seats.ToString(), $"{seats * 0.4:F1}", $"{dockAreaM2:F1}"]);
                }
                if (incKids && kidsPlayAreaM2 > 0.01)
                {
                    t2.Add([$"{t2Num++}. Детский уголок ресторана", "0.2", seats.ToString(), $"{seats * 0.2:F1}", $"{kidsPlayAreaM2:F1}"]);
                }
                t2.Add([$"{t2Num++}. Озеленение участка", "1.5", seats.ToString(), $"{seats * 1.5:F1}", $"{actualGreenM2:F1}"]);

                return new SitePlanExplicationData(
                    BuildingPurposeName: buildingPurposeName,
                    PurposeCategory: purposeCategory,
                    Table1Rows: t1,
                    Table2Rows: t2,
                    Calc1Header: "Расчет вместимости ресторана",
                    Calc1Formula: "S / 4.5 = N (посадочных мест)",
                    Calc1Desc: "где: S — площадь залов, 4.5 м²/место — норма (СП 118.13330)",
                    Calc1Result: $"{buildingAreaM2:F1} / 4.5 = {seats} посадочных мест",
                    Calc2Header: "Расчет ТБО предприятия питания",
                    Calc2Formula: "M = (N × 1.45 кг × 1.2) / 365",
                    Calc2Result: $"Суточное накопление: {dailyWasteM3:F2} м³/сут ({wasteBins} конт. по 0.75 м³)",
                    BuildingAreaM2: buildingAreaM2,
                    RoadAreaM2: roadAreaM2,
                    BlindAreaM2: blindAreaM2,
                    ParkingAreaM2: guestParkingAreaM2 + staffAreaM2,
                    GreenAreaM2: actualGreenM2,
                    TotalSiteAreaM2: totalSiteAreaM2,
                    ParkingSpotsCount: parkingSpotsCount + staffSpots);
            }
            else
            {
                // =========================================================================
                // Б. СТАНДАРТНЫЙ ГЕНПЛАН ЖИЛОГО ДОМА / ДРУГИХ ОБЪЕКТОВ
                // =========================================================================

                // № 1: Здание
                XYZ buildingCenter = new XYZ(bCenterX, bCenterY, baseZ + (2000.0 * MmToFeet));
                CreatePlanNumberCircle(doc, sitePlanView, siteCatId, buildingCenter, 1, 1.6, baseZ, materials);

                // № 6: Временная открытая автостоянка
                List<XYZ> pVerts = GetLoopVertices(parkingLoop);
                XYZ parkCenter = new XYZ(
                    (pVerts.Min(p => p.X) + pVerts.Max(p => p.X)) / 2.0,
                    (pVerts.Min(p => p.Y) + pVerts.Max(p => p.Y)) / 2.0,
                    baseZ);
                CreatePlanNumberCircle(doc, sitePlanView, siteCatId, parkCenter, 6, 1.4, baseZ, materials);

                double playW, playH, playMinX, playMinY, playMaxX, playMaxY;
                double sportW, sportH, sportMinX, sportMinY, sportMaxX, sportMaxY;
                double tennisW, tennisH, tennisMinX, tennisMinY, tennisMaxX, tennisMaxY;
                double restW, restH, restMinX, restMinY, restMaxX, restMaxY;
                double wasteW, wasteH, wasteMinX, wasteMinY, wasteMaxX, wasteMaxY;
                double subW, subH, subMinX, subMinY, subMaxX, subMaxY;
                double hhW, hhH, hhMinX, hhMinY, hhMaxX, hhMaxY;

                switch (effectiveStyle)
                {
                    case SitePlanStyle.CornerHub:
                        playW = 10000.0 * MmToFeet; playH = 8000.0 * MmToFeet;
                        playMinX = Math.Min(sMaxX - playW - (2000.0 * MmToFeet), rMaxX + (1800.0 * MmToFeet));
                        playMaxX = playMinX + playW;
                        playMinY = (rMinY + rMaxY) / 2.0; playMaxY = playMinY + playH;

                        sportW = 12000.0 * MmToFeet; sportH = 8000.0 * MmToFeet;
                        sportMinX = Math.Max(sMinX + (2000.0 * MmToFeet), bCenterX - (sportW / 2.0));
                        sportMaxX = sportMinX + sportW;
                        sportMinY = Math.Min(sMaxY - sportH - (2000.0 * MmToFeet), rMaxY + (2000.0 * MmToFeet));
                        sportMaxY = sportMinY + sportH;

                        tennisW = 6500.0 * MmToFeet; tennisH = 5000.0 * MmToFeet;
                        tennisMinX = Math.Min(sMaxX - tennisW - (2000.0 * MmToFeet), sportMaxX + (1500.0 * MmToFeet));
                        tennisMaxX = tennisMinX + tennisW;
                        tennisMinY = sportMinY; tennisMaxY = tennisMinY + tennisH;

                        restW = 6500.0 * MmToFeet; restH = 5000.0 * MmToFeet;
                        restMinX = Math.Max(sMinX + (2000.0 * MmToFeet), rMinX - restW - (2000.0 * MmToFeet));
                        restMaxX = restMinX + restW;
                        restMinY = (rMinY + rMaxY) / 2.0; restMaxY = restMinY + restH;

                        wasteW = 5500.0 * MmToFeet; wasteH = 3000.0 * MmToFeet;
                        wasteMinX = Math.Max(sMinX + (2500.0 * MmToFeet), rMinX - wasteW - (800.0 * MmToFeet));
                        wasteMaxX = wasteMinX + wasteW;
                        wasteMinY = sMinY + (2000.0 * MmToFeet); wasteMaxY = wasteMinY + wasteH;

                        subW = 4500.0 * MmToFeet; subH = 3500.0 * MmToFeet;
                        subMinX = wasteMaxX + (1500.0 * MmToFeet); subMaxX = subMinX + subW;
                        subMinY = sMinY + (2000.0 * MmToFeet); subMaxY = subMinY + subH;

                        hhW = 5000.0 * MmToFeet; hhH = 3500.0 * MmToFeet;
                        hhMinX = subMaxX + (1500.0 * MmToFeet); hhMaxX = hhMinX + hhW;
                        hhMinY = sMinY + (2000.0 * MmToFeet); hhMaxY = hhMinY + hhH;
                        break;

                    case SitePlanStyle.FrontPlaza:
                        restW = 9000.0 * MmToFeet; restH = 6000.0 * MmToFeet;
                        restMinX = bCenterX - (restW / 2.0); restMaxX = restMinX + restW;
                        restMinY = sMinY + (3000.0 * MmToFeet); restMaxY = restMinY + restH;

                        playW = 9500.0 * MmToFeet; playH = 8000.0 * MmToFeet;
                        playMinX = Math.Min(sMaxX - playW - (2000.0 * MmToFeet), rMaxX + (2500.0 * MmToFeet));
                        playMaxX = playMinX + playW;
                        playMinY = rMinY + (1500.0 * MmToFeet); playMaxY = playMinY + playH;

                        sportW = 13000.0 * MmToFeet; sportH = 8500.0 * MmToFeet;
                        sportMinX = Math.Min(sMaxX - sportW - (2500.0 * MmToFeet), rMaxX + (2000.0 * MmToFeet));
                        sportMaxX = sportMinX + sportW;
                        sportMinY = Math.Min(sMaxY - sportH - (2500.0 * MmToFeet), rMaxY + (2000.0 * MmToFeet));
                        sportMaxY = sportMinY + sportH;

                        tennisW = 7000.0 * MmToFeet; tennisH = 5000.0 * MmToFeet;
                        tennisMinX = bCenterX - (tennisW / 2.0); tennisMaxX = tennisMinX + tennisW;
                        tennisMinY = Math.Min(sMaxY - tennisH - (2500.0 * MmToFeet), rMaxY + (2500.0 * MmToFeet));
                        tennisMaxY = tennisMinY + tennisH;

                        wasteW = 5500.0 * MmToFeet; wasteH = 3000.0 * MmToFeet;
                        wasteMinX = sMinX + (2500.0 * MmToFeet); wasteMaxX = wasteMinX + wasteW;
                        wasteMinY = Math.Min(sMaxY - wasteH - (2500.0 * MmToFeet), rMaxY + (2000.0 * MmToFeet));
                        wasteMaxY = wasteMinY + wasteH;

                        subW = 4500.0 * MmToFeet; subH = 3500.0 * MmToFeet;
                        subMinX = sMinX + (2500.0 * MmToFeet); subMaxX = subMinX + subW;
                        subMinY = Math.Max(sMinY + (2000.0 * MmToFeet), wasteMinY - subH - (1500.0 * MmToFeet));
                        subMaxY = subMinY + subH;

                        hhW = 5000.0 * MmToFeet; hhH = 3500.0 * MmToFeet;
                        hhMinX = sMinX + (2500.0 * MmToFeet); hhMaxX = hhMinX + hhW;
                        hhMinY = Math.Max(sMinY + (2000.0 * MmToFeet), subMinY - hhH - (1500.0 * MmToFeet));
                        hhMaxY = hhMinY + hhH;
                        break;

                    case SitePlanStyle.BusinessHub:
                        restW = 8500.0 * MmToFeet; restH = 5500.0 * MmToFeet;
                        restMinX = Math.Min(sMaxX - restW - (2500.0 * MmToFeet), rMaxX + (2000.0 * MmToFeet));
                        restMaxX = restMinX + restW;
                        restMinY = sMinY + (2500.0 * MmToFeet); restMaxY = restMinY + restH;

                        tennisW = 6500.0 * MmToFeet; tennisH = 5000.0 * MmToFeet;
                        tennisMinX = Math.Min(sMaxX - tennisW - (2500.0 * MmToFeet), rMaxX + (2000.0 * MmToFeet));
                        tennisMaxX = tennisMinX + tennisW;
                        tennisMinY = (rMinY + rMaxY) / 2.0; tennisMaxY = tennisMinY + tennisH;

                        sportW = 10000.0 * MmToFeet; sportH = 8000.0 * MmToFeet;
                        sportMinX = Math.Min(sMaxX - sportW - (2500.0 * MmToFeet), rMaxX + (2000.0 * MmToFeet));
                        sportMaxX = sportMinX + sportW;
                        sportMinY = Math.Min(sMaxY - sportH - (2500.0 * MmToFeet), rMaxY + (2000.0 * MmToFeet));
                        sportMaxY = sportMinY + sportH;

                        playW = 8000.0 * MmToFeet; playH = 7000.0 * MmToFeet;
                        playMinX = Math.Max(sMinX + (2500.0 * MmToFeet), rMinX - playW - (2000.0 * MmToFeet));
                        playMaxX = playMinX + playW;
                        playMinY = (rMinY + rMaxY) / 2.0; playMaxY = playMinY + playH;

                        wasteW = 5500.0 * MmToFeet; wasteH = 3000.0 * MmToFeet;
                        wasteMinX = sMinX + (2500.0 * MmToFeet); wasteMaxX = wasteMinX + wasteW;
                        wasteMinY = Math.Min(sMaxY - wasteH - (2500.0 * MmToFeet), rMaxY + (2000.0 * MmToFeet));
                        wasteMaxY = wasteMinY + wasteH;

                        subW = 5000.0 * MmToFeet; subH = 4000.0 * MmToFeet;
                        subMinX = wasteMaxX + (1500.0 * MmToFeet); subMaxX = subMinX + subW;
                        subMinY = wasteMinY; subMaxY = wasteMinY + subH;

                        hhW = 4500.0 * MmToFeet; hhH = 4000.0 * MmToFeet;
                        hhMinX = subMaxX + (1500.0 * MmToFeet); hhMaxX = hhMinX + hhW;
                        hhMinY = wasteMinY; hhMaxY = wasteMinY + hhH;
                        break;

                    default: // GreenCampus
                        playW = 12000.0 * MmToFeet; playH = 9000.0 * MmToFeet;
                        playMinX = Math.Min(sMaxX - playW - (2500.0 * MmToFeet), rMaxX + (2000.0 * MmToFeet));
                        playMaxX = playMinX + playW;
                        playMinY = sMinY + (2500.0 * MmToFeet); playMaxY = playMinY + playH;

                        restW = 8000.0 * MmToFeet; restH = 6000.0 * MmToFeet;
                        restMinX = Math.Max(sMinX + (2500.0 * MmToFeet), rMinX - restW - (2500.0 * MmToFeet));
                        restMaxX = restMinX + restW;
                        restMinY = (rMinY + rMaxY) / 2.0; restMaxY = restMinY + restH;

                        sportW = 14000.0 * MmToFeet; sportH = 8500.0 * MmToFeet;
                        sportMinX = bCenterX - (sportW / 2.0); sportMaxX = sportMinX + sportW;
                        sportMinY = Math.Min(sMaxY - sportH - (2500.0 * MmToFeet), rMaxY + (2500.0 * MmToFeet));
                        sportMaxY = sportMinY + sportH;

                        tennisW = 7000.0 * MmToFeet; tennisH = 5000.0 * MmToFeet;
                        tennisMinX = Math.Min(sMaxX - tennisW - (2500.0 * MmToFeet), rMaxX + (2000.0 * MmToFeet));
                        tennisMaxX = tennisMinX + tennisW;
                        tennisMinY = (rMinY + rMaxY) / 2.0; tennisMaxY = tennisMinY + tennisH;

                        wasteW = 5500.0 * MmToFeet; wasteH = 3000.0 * MmToFeet;
                        wasteMinX = sMinX + (2500.0 * MmToFeet); wasteMaxX = wasteMinX + wasteW;
                        wasteMinY = sMinY + (2500.0 * MmToFeet); wasteMaxY = wasteMinY + wasteH;

                        subW = 4500.0 * MmToFeet; subH = 3500.0 * MmToFeet;
                        subMinX = wasteMaxX + (1500.0 * MmToFeet); subMaxX = subMinX + subW;
                        subMinY = sMinY + (2500.0 * MmToFeet); subMaxY = wasteMinY + subH;

                        hhW = 5000.0 * MmToFeet; hhH = 3500.0 * MmToFeet;
                        hhMinX = subMaxX + (1500.0 * MmToFeet); hhMaxX = hhMinX + hhW;
                        hhMinY = sMinY + (2500.0 * MmToFeet); hhMaxY = wasteMinY + hhH;
                        break;
                }

                // № 2: Детская площадка
                CurveLoop playLoop = CreateRectangleLoop(playMinX, playMinY, playMaxX, playMaxY, baseZ - (20.0 * MmToFeet));
                Create3DExtrusionDirectShape(doc, siteCatId, [playLoop], 100.0 * MmToFeet, materials.PlaygroundYellowMaterialId,
                    "ГП_3D: Площадка для игр детей (№ 2)", "2");
                CurveLoop playCurbOut = CreateRectangleLoop(playMinX - curbW, playMinY - curbW, playMaxX + curbW, playMaxY + curbW, baseZ - (80.0 * MmToFeet));
                CurveLoop playCurbIn = CreateRectangleLoop(playMinX, playMinY, playMaxX, playMaxY, baseZ - (80.0 * MmToFeet));
                playCurbIn.Flip();
                Create3DExtrusionDirectShape(doc, siteCatId, [playCurbOut, playCurbIn], 160.0 * MmToFeet, materials.CurbConcreteMaterialId,
                    "ГП_3D: Бордюр детской площадки", "ГП-БОРД");

                List<GeometryObject> playMafSolids = [];
                CurveLoop sandboxOuter = CreateRectangleLoop(playMinX + (800.0 * MmToFeet), playMinY + (800.0 * MmToFeet),
                    playMinX + (3600.0 * MmToFeet), playMinY + (3600.0 * MmToFeet), baseZ);
                playMafSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([sandboxOuter], XYZ.BasisZ, 300.0 * MmToFeet,
                    new SolidOptions(materials.WoodBenchMaterialId, ElementId.InvalidElementId)));
                CurveLoop sandboxSand = CreateRectangleLoop(playMinX + (950.0 * MmToFeet), playMinY + (950.0 * MmToFeet),
                    playMinX + (3450.0 * MmToFeet), playMinY + (3450.0 * MmToFeet), baseZ);
                playMafSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([sandboxSand], XYZ.BasisZ, 250.0 * MmToFeet,
                    new SolidOptions(materials.YellowStopperMaterialId, ElementId.InvalidElementId)));
                playMafSolids.AddRange(CreateBenchSolid(new XYZ(playMaxX - (900.0 * MmToFeet), (playMinY + playMaxY) / 2.0, baseZ), Math.PI / 2.0, materials));
                if (playMafSolids.Count > 0)
                {
                    DirectShape pMaf = DirectShape.CreateElement(doc, siteCatId);
                    pMaf.SetShape(playMafSolids);
                    SetZoneMetadata(pMaf, "ГП_3D: МАФ детской площадки", "ГП-МАФ");
                }
                XYZ playCenter = new XYZ((playMinX + playMaxX) / 2.0, (playMinY + playMaxY) / 2.0, baseZ);
                CreatePlanNumberCircle(doc, sitePlanView, siteCatId, playCenter, 2, 1.4, baseZ, materials);
                double playgroundAreaM2 = Math.Round((playMaxX - playMinX) * (playMaxY - playMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);

                // № 3: Спортивная площадка
                CurveLoop sportLoop = CreateRectangleLoop(sportMinX, sportMinY, sportMaxX, sportMaxY, baseZ - (20.0 * MmToFeet));
                Create3DExtrusionDirectShape(doc, siteCatId, [sportLoop], 100.0 * MmToFeet, materials.SportsCourtOrangeMaterialId,
                    "ГП_3D: Спортивная площадка (№ 3)", "3");
                CurveLoop sportCurbOut = CreateRectangleLoop(sportMinX - curbW, sportMinY - curbW, sportMaxX + curbW, sportMaxY + curbW, baseZ - (80.0 * MmToFeet));
                CurveLoop sportCurbIn = CreateRectangleLoop(sportMinX, sportMinY, sportMaxX, sportMaxY, baseZ - (80.0 * MmToFeet));
                sportCurbIn.Flip();
                Create3DExtrusionDirectShape(doc, siteCatId, [sportCurbOut, sportCurbIn], 160.0 * MmToFeet, materials.CurbConcreteMaterialId,
                    "ГП_3D: Бордюр спортивной площадки", "ГП-БОРД");

                List<GeometryObject> sportMarkings = [];
                double mThickness = 80.0 * MmToFeet;
                double mZ = baseZ + (80.0 * MmToFeet);
                sportMarkings.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    [CreateRectangleLoop(sportMinX + (400.0 * MmToFeet), sportMinY + (400.0 * MmToFeet), sportMaxX - (400.0 * MmToFeet), sportMinY + (400.0 * MmToFeet) + mThickness, mZ)],
                    XYZ.BasisZ, 12.0 * MmToFeet, new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));
                sportMarkings.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    [CreateRectangleLoop(sportMinX + (400.0 * MmToFeet), sportMaxY - (400.0 * MmToFeet) - mThickness, sportMaxX - (400.0 * MmToFeet), sportMaxY - (400.0 * MmToFeet), mZ)],
                    XYZ.BasisZ, 12.0 * MmToFeet, new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));
                sportMarkings.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    [CreateRectangleLoop(sportMinX + (400.0 * MmToFeet), sportMinY + (400.0 * MmToFeet), sportMinX + (400.0 * MmToFeet) + mThickness, sportMaxY - (400.0 * MmToFeet), mZ)],
                    XYZ.BasisZ, 12.0 * MmToFeet, new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));
                sportMarkings.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    [CreateRectangleLoop(sportMaxX - (400.0 * MmToFeet) - mThickness, sportMinY + (400.0 * MmToFeet), sportMaxX - (400.0 * MmToFeet), sportMaxY - (400.0 * MmToFeet), mZ)],
                    XYZ.BasisZ, 12.0 * MmToFeet, new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));

                double sMidX = (sportMinX + sportMaxX) / 2.0;
                sportMarkings.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    [CreateRectangleLoop(sMidX - (mThickness / 2.0), sportMinY + (400.0 * MmToFeet), sMidX + (mThickness / 2.0), sportMaxY - (400.0 * MmToFeet), mZ)],
                    XYZ.BasisZ, 12.0 * MmToFeet, new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));

                DirectShape sMarkDs = DirectShape.CreateElement(doc, siteCatId);
                sMarkDs.SetShape(sportMarkings);
                SetZoneMetadata(sMarkDs, "ГП_3D: Разметка спортивной площадки", "ГП-РАЗМ");

                XYZ sportCenter = new XYZ((sportMinX + sportMaxX) / 2.0, (sportMinY + sportMaxY) / 2.0, baseZ);
                CreatePlanNumberCircle(doc, sitePlanView, siteCatId, sportCenter, 3, 1.4, baseZ, materials);
                double sportsAreaM2 = Math.Round((sportMaxX - sportMinX) * (sportMaxY - sportMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);

                // № 4: Настольный теннис
                CurveLoop tennisLoop = CreateRectangleLoop(tennisMinX, tennisMinY, tennisMaxX, tennisMaxY, baseZ - (20.0 * MmToFeet));
                Create3DExtrusionDirectShape(doc, siteCatId, [tennisLoop], 100.0 * MmToFeet, materials.SportsCourtOrangeMaterialId,
                    "ГП_3D: Площадка для настольного тенниса (№ 4)", "4");
                CurveLoop tennisCurbOut = CreateRectangleLoop(tennisMinX - curbW, tennisMinY - curbW, tennisMaxX + curbW, tennisMaxY + curbW, baseZ - (80.0 * MmToFeet));
                CurveLoop tennisCurbIn = CreateRectangleLoop(tennisMinX, tennisMinY, tennisMaxX, tennisMaxY, baseZ - (80.0 * MmToFeet));
                tennisCurbIn.Flip();
                Create3DExtrusionDirectShape(doc, siteCatId, [tennisCurbOut, tennisCurbIn], 160.0 * MmToFeet, materials.CurbConcreteMaterialId,
                    "ГП_3D: Бордюр теннисной зоны", "ГП-БОРД");

                List<GeometryObject> tennisTableSolids = [];
                double ttCenterX = (tennisMinX + tennisMaxX) / 2.0;
                double ttCenterY = (tennisMinY + tennisMaxY) / 2.0;
                double ttHalfLen = 1370.0 * MmToFeet;
                double ttHalfWid = 762.5 * MmToFeet;
                double ttTableZ = baseZ + (760.0 * MmToFeet);

                CurveLoop tableTop = CreateRectangleLoop(ttCenterX - ttHalfLen, ttCenterY - ttHalfWid,
                    ttCenterX + ttHalfLen, ttCenterY + ttHalfWid, ttTableZ);
                tennisTableSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([tableTop], XYZ.BasisZ, 45.0 * MmToFeet,
                    new SolidOptions(materials.ShrubFoliageMaterialId, ElementId.InvalidElementId)));
                CurveLoop netLoop = CreateRectangleLoop(ttCenterX - (15.0 * MmToFeet), ttCenterY - ttHalfWid - (100.0 * MmToFeet),
                    ttCenterX + (15.0 * MmToFeet), ttCenterY + ttHalfWid + (100.0 * MmToFeet), ttTableZ + (45.0 * MmToFeet));
                tennisTableSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([netLoop], XYZ.BasisZ, 150.0 * MmToFeet,
                    new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));

                double legR = 35.0 * MmToFeet;
                XYZ[] legPositions =
                [
                    new XYZ(ttCenterX - ttHalfLen + (300.0 * MmToFeet), ttCenterY - ttHalfWid + (200.0 * MmToFeet), baseZ),
                    new XYZ(ttCenterX + ttHalfLen - (300.0 * MmToFeet), ttCenterY - ttHalfWid + (200.0 * MmToFeet), baseZ),
                    new XYZ(ttCenterX - ttHalfLen + (300.0 * MmToFeet), ttCenterY + ttHalfWid - (200.0 * MmToFeet), baseZ),
                    new XYZ(ttCenterX + ttHalfLen - (300.0 * MmToFeet), ttCenterY + ttHalfWid - (200.0 * MmToFeet), baseZ)
                ];
                foreach (XYZ lp in legPositions)
                {
                    CurveLoop legLoop = CreateOctagonLoop(lp.X, lp.Y, legR, lp.Z);
                    tennisTableSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([legLoop], XYZ.BasisZ, 760.0 * MmToFeet,
                        new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));
                }

                DirectShape ttDs = DirectShape.CreateElement(doc, siteCatId);
                ttDs.SetShape(tennisTableSolids);
                SetZoneMetadata(ttDs, "ГП_3D: Стол для настольного тенниса", "ГП-МАФ");

                XYZ tennisCenter = new XYZ(ttCenterX, tennisMaxY - (1200.0 * MmToFeet), baseZ);
                CreatePlanNumberCircle(doc, sitePlanView, siteCatId, tennisCenter, 4, 1.3, baseZ, materials);
                double tennisAreaM2 = Math.Round((tennisMaxX - tennisMinX) * (tennisMaxY - tennisMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);

                // № 5: Площадка для отдыха взрослых
                CurveLoop restLoop = CreateRectangleLoop(restMinX, restMinY, restMaxX, restMaxY, baseZ - (20.0 * MmToFeet));
                Create3DExtrusionDirectShape(doc, siteCatId, [restLoop], 100.0 * MmToFeet, materials.PavingTileMaterialId,
                    "ГП_3D: Площадка для отдыха взрослых (№ 5)", "5");
                CurveLoop restCurbOut = CreateRectangleLoop(restMinX - curbW, restMinY - curbW, restMaxX + curbW, restMaxY + curbW, baseZ - (80.0 * MmToFeet));
                CurveLoop restCurbIn = CreateRectangleLoop(restMinX, restMinY, restMaxX, restMaxY, baseZ - (80.0 * MmToFeet));
                restCurbIn.Flip();
                Create3DExtrusionDirectShape(doc, siteCatId, [restCurbOut, restCurbIn], 160.0 * MmToFeet, materials.CurbConcreteMaterialId,
                    "ГП_3D: Бордюр площадки отдыха", "ГП-БОРД");

                List<GeometryObject> restMaf = [];
                double restMidY = (restMinY + restMaxY) / 2.0;
                restMaf.AddRange(CreateBenchSolid(new XYZ(restMinX + (1200.0 * MmToFeet), restMidY, baseZ), Math.PI / 2.0, materials));
                restMaf.AddRange(CreateBenchSolid(new XYZ(restMaxX - (1200.0 * MmToFeet), restMidY, baseZ), -Math.PI / 2.0, materials));
                DirectShape restMafDs = DirectShape.CreateElement(doc, siteCatId);
                restMafDs.SetShape(restMaf);
                SetZoneMetadata(restMafDs, "ГП_3D: Скамейки зоны отдыха взрослых", "ГП-МАФ");

                XYZ restCenter = new XYZ((restMinX + restMaxX) / 2.0, (restMinY + restMaxY) / 2.0, baseZ);
                CreatePlanNumberCircle(doc, sitePlanView, siteCatId, restCenter, 5, 1.3, baseZ, materials);
                double adultRestAreaM2 = Math.Round((restMaxX - restMinX) * (restMaxY - restMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);

                // № 7: Площадка для сбора ТБО
                CurveLoop wasteLoop = CreateRectangleLoop(wasteMinX, wasteMinY, wasteMaxX, wasteMaxY, baseZ - (20.0 * MmToFeet));
                Create3DExtrusionDirectShape(doc, siteCatId, [wasteLoop], 100.0 * MmToFeet, materials.WasteSiteMaterialId,
                    "ГП_3D: Площадка для сбора ТБО (№ 7)", "7");
                List<GeometryObject> wasteSolids = CreateWasteEnclosureAndBins(wasteMinX, wasteMinY, wasteMaxX, wasteMaxY, baseZ, materials);
                DirectShape wasteDs = DirectShape.CreateElement(doc, siteCatId);
                wasteDs.SetShape(wasteSolids);
                SetZoneMetadata(wasteDs, "ГП_3D: Ограждение и контейнеры ТБО", "ГП-МАФ");

                XYZ wasteCenter = new XYZ((wasteMinX + wasteMaxX) / 2.0, (wasteMinY + wasteMaxY) / 2.0, baseZ);
                CreatePlanNumberCircle(doc, sitePlanView, siteCatId, wasteCenter, 7, 1.3, baseZ, materials);
                double wasteAreaM2 = Math.Round((wasteMaxX - wasteMinX) * (wasteMaxY - wasteMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);

                // № 8: Котельная / ТП
                List<GeometryObject> subSolids = CreateSubstationBuildingSolid(subMinX, subMinY, subMaxX, subMaxY, baseZ, materials);
                DirectShape subDs = DirectShape.CreateElement(doc, siteCatId);
                subDs.SetShape(subSolids);
                SetZoneMetadata(subDs, "ГП_3D: Блочная котельная / ТП (№ 8)", "8");

                XYZ subCenter = new XYZ((subMinX + subMaxX) / 2.0, (subMinY + subMaxY) / 2.0, baseZ + (3000.0 * MmToFeet));
                CreatePlanNumberCircle(doc, sitePlanView, siteCatId, subCenter, 8, 1.3, baseZ, materials);
                double utilityAreaM2 = Math.Round((subMaxX - subMinX) * (subMaxY - subMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);

                // № 9: Хозяйственная площадка
                CurveLoop hhLoop = CreateRectangleLoop(hhMinX, hhMinY, hhMaxX, hhMaxY, baseZ - (20.0 * MmToFeet));
                Create3DExtrusionDirectShape(doc, siteCatId, [hhLoop], 100.0 * MmToFeet, materials.PavingTileMaterialId,
                    "ГП_3D: Хозяйственная площадка (№ 9)", "9");
                CurveLoop hhCurbOut = CreateRectangleLoop(hhMinX - curbW, hhMinY - curbW, hhMaxX + curbW, hhMaxY + curbW, baseZ - (80.0 * MmToFeet));
                CurveLoop hhCurbIn = CreateRectangleLoop(hhMinX, hhMinY, hhMaxX, hhMaxY, baseZ - (80.0 * MmToFeet));
                hhCurbIn.Flip();
                Create3DExtrusionDirectShape(doc, siteCatId, [hhCurbOut, hhCurbIn], 160.0 * MmToFeet, materials.CurbConcreteMaterialId,
                    "ГП_3D: Бордюр хозяйственной площадки", "ГП-БОРД");

                List<GeometryObject> hhMafSolids = [];
                double hhMidY = (hhMinY + hhMaxY) / 2.0;
                double postR = 35.0 * MmToFeet;
                double postH = 2100.0 * MmToFeet;
                XYZ post1 = new XYZ(hhMinX + (1200.0 * MmToFeet), hhMidY, baseZ);
                XYZ post2 = new XYZ(hhMaxX - (1200.0 * MmToFeet), hhMidY, baseZ);
                hhMafSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([CreateOctagonLoop(post1.X, post1.Y, postR, post1.Z)],
                    XYZ.BasisZ, postH, new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));
                hhMafSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([CreateOctagonLoop(post2.X, post2.Y, postR, post2.Z)],
                    XYZ.BasisZ, postH, new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));
                CurveLoop crossbar = CreateRectangleLoop(post1.X - postR, hhMidY - postR, post2.X + postR, hhMidY + postR, baseZ + postH - (70.0 * MmToFeet));
                hhMafSolids.Add(GeometryCreationUtilities.CreateExtrusionGeometry([crossbar], XYZ.BasisZ, 70.0 * MmToFeet,
                    new SolidOptions(materials.MetalDarkMaterialId, ElementId.InvalidElementId)));
                DirectShape hhDs = DirectShape.CreateElement(doc, siteCatId);
                hhDs.SetShape(hhMafSolids);
                SetZoneMetadata(hhDs, "ГП_3D: Стойка для чистки ковров и сушки белья", "ГП-МАФ");

                XYZ hhCenter = new XYZ((hhMinX + hhMaxX) / 2.0, (hhMinY + hhMaxY) / 2.0, baseZ);
                CreatePlanNumberCircle(doc, sitePlanView, siteCatId, hhCenter, 9, 1.3, baseZ, materials);
                double householdAreaM2 = Math.Round((hhMaxX - hhMinX) * (hhMaxY - hhMinY) / (MmToFeet * MmToFeet * 1000000.0), 1);

                // Баланс территории
                double sumConstructed = buildingAreaM2 + blindAreaM2 + roadAreaM2 + parkingAreaM2 +
                                        playgroundAreaM2 + sportsAreaM2 + tennisAreaM2 + adultRestAreaM2 +
                                        wasteAreaM2 + utilityAreaM2 + householdAreaM2;
                double actualGreenM2 = Math.Max(0.0, totalSiteAreaM2 - sumConstructed);

                double estimatedLivingS = buildingAreaM2 * 3.5;
                int population = Math.Max(35, (int)Math.Round(estimatedLivingS / 25.5));
                double dailyWasteM3 = (population * 1.84 * 1.25) / 365.0;
                int wasteContainers = Math.Max(2, (int)Math.Ceiling(dailyWasteM3 / (0.9 * 0.75)));

                List<string[]> t1 =
                [
                    ["1", $"Проектируемое здание ({buildingPurposeName})", $"Застройка ({buildingAreaM2:F1} м²)"],
                    ["2", "Площадка для игр детей", $"Песчано-полимерное покр. ({playgroundAreaM2:F1} м²)"],
                    ["3", "Спортивная площадка для занятий физкультурой", $"Спортивное покрытие ({sportsAreaM2:F1} м²)"],
                    ["4", "Площадка для настольного тенниса", $"Оборудованная зона ({tennisAreaM2:F1} м²)"],
                    ["5", "Площадка для отдыха взрослого населения", $"Плиточное мощение ({adultRestAreaM2:F1} м²)"],
                    ["6", "Временная открытая автостоянка", $"В т.ч. гостевая ({parkingSpotsCount} м/м, {parkingAreaM2:F1} м²)"],
                    ["7", "Площадка для сбора бытовых отходов (ТБО)", $"{wasteContainers} контейнера по 0.75 м³ ({wasteAreaM2:F1} м²)"],
                    ["8", "Блочная котельная / Трансформаторная подстанция (ТП)", $"Инженерное сооружение ({utilityAreaM2:F1} м²)"],
                    ["9", "Хозяйственная площадка (чистка ковров, сушка)", $"Оборудованная стойками ({householdAreaM2:F1} м²)"]
                ];

                List<string[]> t2 =
                [
                    ["1. Для игр детей", "0.7", population.ToString(), $"{population * 0.7:F1}", $"{playgroundAreaM2:F1}"],
                    ["2. Для занятий физкультурой", "2.0", population.ToString(), $"{population * 2.0:F1}", $"{sportsAreaM2 + tennisAreaM2:F1}"],
                    ["3. Для отдыха взрослых", "0.1", population.ToString(), $"{population * 0.1:F1}", $"{adultRestAreaM2:F1}"],
                    ["4. Для хозяйственных целей (в т.ч. ТБО)", "0.3", population.ToString(), $"{population * 0.3:F1}", $"{wasteAreaM2 + householdAreaM2:F1}"],
                    ["5. Для парковки автомобилей", "1.2", population.ToString(), $"{population * 1.2:F1}", $"{parkingAreaM2:F1}"],
                    ["6. Озеленение территории", "2.0", population.ToString(), $"{population * 2.0:F1}", $"{actualGreenM2:F1}"]
                ];

                return new SitePlanExplicationData(
                    BuildingPurposeName: buildingPurposeName,
                    PurposeCategory: purposeCategory,
                    Table1Rows: t1,
                    Table2Rows: t2,
                    Calc1Header: "Расчет количества населения",
                    Calc1Formula: "S / n = N",
                    Calc1Desc: "где: S — площадь квартир, n — норма жил. обеспеченности (25.5 м²/чел.)",
                    Calc1Result: $"{estimatedLivingS:F1} / 25.5 = {population} чел.",
                    Calc2Header: "Расчет ТБО",
                    Calc2Formula: "M = (1.84 × N × 1.25) / (365 × 0.9 × 0.75)",
                    Calc2Result: $"Потребное количество контейнеров: {wasteContainers} (конт. по 0.75 м³)",
                    BuildingAreaM2: buildingAreaM2,
                    RoadAreaM2: roadAreaM2,
                    BlindAreaM2: blindAreaM2,
                    ParkingAreaM2: parkingAreaM2,
                    GreenAreaM2: actualGreenM2,
                    TotalSiteAreaM2: totalSiteAreaM2,
                    ParkingSpotsCount: parkingSpotsCount);
            }
        }

        // =========================================================================
        // ОФОРМЛЕНИЕ ЛИСТА ГП ПО ГОСТ: ТАБЛИЦЫ С ДИНАМИЧЕСКОЙ ВЫСОТОЙ (БЕЗ НАЛОЖЕНИЙ)
        // =========================================================================

        private static void EnsureSheetSketchPlane(Document doc, ViewSheet sheet)
        {
            if (sheet.SketchPlane == null)
            {
                try
                {
                    Plane plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    SketchPlane sp = SketchPlane.Create(doc, plane);
                    sheet.SketchPlane = sp;
                }
                catch
                {
                }
            }
        }

        private static DetailCurve? DrawSheetLine(Document doc, ViewSheet sheet, XYZ p0, XYZ p1)
        {
            if (p0.DistanceTo(p1) < 1e-4)
            {
                return null;
            }

            try
            {
                Line line = Line.CreateBound(p0, p1);
                return doc.Create.NewDetailCurve(sheet, line);
            }
            catch
            {
                return null;
            }
        }

        private static TextNote? DrawSheetText(
            Document doc,
            ViewSheet sheet,
            XYZ origin,
            string text,
            ElementId textTypeId,
            HorizontalTextAlignment align = HorizontalTextAlignment.Left,
            double widthFt = 0.0)
        {
            if (string.IsNullOrEmpty(text) || textTypeId == ElementId.InvalidElementId)
            {
                return null;
            }

            try
            {
                TextNoteOptions options = new TextNoteOptions(textTypeId)
                {
                    HorizontalAlignment = align,
                    Rotation = 0.0
                };
                if (widthFt > 1e-4)
                {
                    return TextNote.Create(doc, sheet.Id, origin, widthFt, text, options);
                }
                return TextNote.Create(doc, sheet.Id, origin, text, options);
            }
            catch
            {
                return null;
            }
        }

        private static ElementId GetOrCreateGostTextNoteTypeId(Document doc, double heightMm, bool bold, string suffix)
        {
            string typeName = $"ГП_Текст_{suffix}";
            TextNoteType? existing = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                return existing.Id;
            }

            TextNoteType? baseType = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault(t => t.Name.Contains("ISOCPEUR") || t.Name.Contains("ГОСТ") || t.Name.Contains("Arial"))
                ?? new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .FirstOrDefault();

            if (baseType == null)
            {
                return ElementId.InvalidElementId;
            }

            try
            {
                TextNoteType newType = (TextNoteType)baseType.Duplicate(typeName);
                newType.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(heightMm * MmToFeet);
                newType.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set("ISOCPEUR");
                newType.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)?.Set(bold ? 1 : 0);
                newType.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1); // 1 = Transparent
                return newType.Id;
            }
            catch
            {
                return baseType.Id;
            }
        }

        private static int EstimateLineCount(string text, double availWidthMm)
        {
            if (string.IsNullOrWhiteSpace(text)) return 1;
            string[] rawLines = text.Split('\n');
            int total = 0;
            double charWidthMm = 1.35;
            foreach (string line in rawLines)
            {
                double lineLenMm = line.Length * charWidthMm;
                int wrapped = (int)Math.Ceiling(lineLenMm / Math.Max(10.0, availWidthMm));
                total += Math.Max(1, wrapped);
            }
            return Math.Max(1, total);
        }

        private static double DrawSheetTable(
            Document doc,
            ViewSheet sheet,
            double tableLeftX,
            double tableTopY,
            double[] colWidthsMm,
            string[] headerTitles,
            List<string[]> rowValues,
            double minHeaderHeightMm,
            double minRowHeightMm,
            ElementId fontHeaderTypeId,
            ElementId fontCellTypeId,
            ElementId fontBoldCellTypeId)
        {
            int numCols = colWidthsMm.Length;
            double totalWidthMm = colWidthsMm.Sum();

            // 1. Динамический расчет высоты шапки
            int maxHeaderLines = 1;
            for (int c = 0; c < numCols; c++)
            {
                string h = c < headerTitles.Length ? headerTitles[c] : "";
                int lines = EstimateLineCount(h, colWidthsMm[c] - 2.0);
                if (lines > maxHeaderLines) maxHeaderLines = lines;
            }
            double headerHeightMm = Math.Max(minHeaderHeightMm, (maxHeaderLines * 3.5) + 3.0);

            // 2. Динамический расчет высоты каждой строки с запасом под многострочные переносы
            List<double> rowHeightsMm = [];
            for (int r = 0; r < rowValues.Count; r++)
            {
                int maxLines = 1;
                for (int c = 0; c < numCols; c++)
                {
                    string val = c < rowValues[r].Length ? rowValues[r][c] : "";
                    int lines = EstimateLineCount(val, colWidthsMm[c] - 3.0);
                    if (lines > maxLines) maxLines = lines;
                }
                double h = Math.Max(minRowHeightMm, (maxLines * 3.4) + 2.4);
                rowHeightsMm.Add(h);
            }

            double totalRowsHeightMm = rowHeightsMm.Sum();
            double totalHeightMm = headerHeightMm + totalRowsHeightMm;

            double totalWidthFt = totalWidthMm * MmToFeet;
            double totalHeightFt = totalHeightMm * MmToFeet;
            double tableRightX = tableLeftX + totalWidthFt;
            double tableBottomY = tableTopY - totalHeightFt;

            // 3. Внешняя рамка таблицы
            DrawSheetLine(doc, sheet, new XYZ(tableLeftX, tableTopY, 0.0), new XYZ(tableRightX, tableTopY, 0.0));
            DrawSheetLine(doc, sheet, new XYZ(tableRightX, tableTopY, 0.0), new XYZ(tableRightX, tableBottomY, 0.0));
            DrawSheetLine(doc, sheet, new XYZ(tableRightX, tableBottomY, 0.0), new XYZ(tableLeftX, tableBottomY, 0.0));
            DrawSheetLine(doc, sheet, new XYZ(tableLeftX, tableBottomY, 0.0), new XYZ(tableLeftX, tableTopY, 0.0));

            // 4. Линия под шапкой таблицы
            double headerBottomY = tableTopY - (headerHeightMm * MmToFeet);
            DrawSheetLine(doc, sheet, new XYZ(tableLeftX, headerBottomY, 0.0), new XYZ(tableRightX, headerBottomY, 0.0));

            // 5. Горизонтальные разделители строк
            double currLineY = headerBottomY;
            for (int r = 0; r < rowValues.Count - 1; r++)
            {
                currLineY -= rowHeightsMm[r] * MmToFeet;
                DrawSheetLine(doc, sheet, new XYZ(tableLeftX, currLineY, 0.0), new XYZ(tableRightX, currLineY, 0.0));
            }

            // 6. Вертикальные линии колонок и заголовки шапки
            double currX = tableLeftX;
            for (int c = 0; c < numCols; c++)
            {
                double colWFt = colWidthsMm[c] * MmToFeet;
                if (c > 0)
                {
                    DrawSheetLine(doc, sheet, new XYZ(currX, tableTopY, 0.0), new XYZ(currX, tableBottomY, 0.0));
                }

                double cellCenterX = currX + (colWFt / 2.0);
                double cellTopY = tableTopY - (1.6 * MmToFeet);
                string hText = c < headerTitles.Length ? headerTitles[c] : "";
                DrawSheetText(doc, sheet, new XYZ(cellCenterX, cellTopY, 0.0), hText, fontCellTypeId, HorizontalTextAlignment.Center, colWFt - (1.5 * MmToFeet));

                currX += colWFt;
            }

            // 7. Текст ячеек таблицы
            double rowTopY = headerBottomY;
            for (int r = 0; r < rowValues.Count; r++)
            {
                string[] row = rowValues[r];
                double cellY = rowTopY - (1.5 * MmToFeet);
                double colX = tableLeftX;

                for (int c = 0; c < numCols; c++)
                {
                    double colWFt = colWidthsMm[c] * MmToFeet;
                    string val = c < row.Length ? row[c] : "";

                    bool isSummaryRow = r >= rowValues.Count - 2 && (rowValues[r].Length > 1 && rowValues[r][1].StartsWith("Итого", StringComparison.OrdinalIgnoreCase));
                    bool isCenterCol = c == 0 || (numCols == 5 && c >= 1) || (numCols == 4 && c >= 2);
                    ElementId fId = (c == 0 || isSummaryRow) ? fontBoldCellTypeId : fontCellTypeId;
                    if (isCenterCol)
                    {
                        double centerX = colX + (colWFt / 2.0);
                        DrawSheetText(doc, sheet, new XYZ(centerX, cellY, 0.0), val, fId, HorizontalTextAlignment.Center);
                    }
                    else
                    {
                        double leftX = colX + (1.5 * MmToFeet);
                        DrawSheetText(doc, sheet, new XYZ(leftX, cellY, 0.0), val, fId, HorizontalTextAlignment.Left, colWFt - (2.5 * MmToFeet));
                    }

                    colX += colWFt;
                }

                rowTopY -= rowHeightsMm[r] * MmToFeet;
            }

            return totalHeightMm;
        }

        private static void DrawGostSiteExplicationTablesOnSheet(
            Document doc,
            ViewSheet sheet,
            double originX,
            double originY,
            SitePlanExplicationData data,
            SitePlanEditorConfig? editorConfig = null)
        {
            EnsureSheetSketchPlane(doc, sheet);

            ElementId fontHeaderTypeId = GetOrCreateGostTextNoteTypeId(doc, 3.2, bold: true, suffix: "ЗаголовокТаблицы_3.2мм");
            ElementId fontCellTypeId = GetOrCreateGostTextNoteTypeId(doc, 2.3, bold: false, suffix: "ЯчейкаТаблицы_2.3мм");
            ElementId fontBoldCellTypeId = GetOrCreateGostTextNoteTypeId(doc, 2.3, bold: true, suffix: "ЯчейкаЖирная_2.3мм");
            ElementId fontCalcTextTypeId = GetOrCreateGostTextNoteTypeId(doc, 2.1, bold: false, suffix: "РасчетТекст_2.1мм");
            ElementId fontCalcFormulaTypeId = GetOrCreateGostTextNoteTypeId(doc, 2.5, bold: true, suffix: "РасчетФормула_2.5мм");

            double sheetRightX = originX + ((SheetWidthMm - MarginRightMm) * MmToFeet);
            double colWidthMm = 140.0;
            double colLeftX = sheetRightX - (colWidthMm * MmToFeet);
            double currentTopY = originY + ((SheetHeightMm - MarginTopMm - 4.0) * MmToFeet);

            // =========================================================================
            // 0. ТАБЛИЦА 0: "Экспликация зон и баланс территории" (ГОСТ 21.508-2020)
            // =========================================================================
            double bldgArea = editorConfig?.BuildingAreaM2 ?? data.BuildingAreaM2;
            double pavArea = editorConfig?.PavingAreaM2 ?? data.BlindAreaM2;
            double asphArea = editorConfig?.AsphaltAreaM2 ?? (data.RoadAreaM2 + data.ParkingAreaM2);
            double totalArea = editorConfig?.TotalSiteAreaM2 ?? data.TotalSiteAreaM2;
            if (totalArea <= 0.0) totalArea = bldgArea + pavArea + asphArea + data.GreenAreaM2;
            double greenArea = editorConfig?.GreenAreaM2 ?? Math.Max(0.0, totalArea - bldgArea - pavArea - asphArea);
            if (greenArea <= 0.0 && data.GreenAreaM2 > 0.0) greenArea = data.GreenAreaM2;

            double pctBldg = totalArea > 0.0 ? (bldgArea / totalArea * 100.0) : 0.0;
            double pctAsph = totalArea > 0.0 ? (asphArea / totalArea * 100.0) : 0.0;
            double pctPav = totalArea > 0.0 ? (pavArea / totalArea * 100.0) : 0.0;
            double pctGreen = totalArea > 0.0 ? (greenArea / totalArea * 100.0) : 0.0;
            double kz = totalArea > 0.0 ? (bldgArea / totalArea) : 0.0;
            double koz = totalArea > 0.0 ? (greenArea / totalArea) : 0.0;

            DrawSheetText(doc, sheet,
                new XYZ(colLeftX + ((colWidthMm / 2.0) * MmToFeet), currentTopY, 0.0),
                "Экспликация зон и баланс территории",
                fontHeaderTypeId, HorizontalTextAlignment.Center);

            currentTopY -= 5.0 * MmToFeet;

            double[] t0Cols = [14.0, 70.0, 30.0, 26.0];
            string[] t0Header = ["№", "Наименование зоны", "Площадь, м²", "Доля, %"];
            List<string[]> t0Rows =
            [
                ["1", "Зона застройки (здание)", $"{bldgArea:F1}", $"{pctBldg:F1} %"],
                ["2", "Асфальтированные зоны (проезды, стоянки)", $"{asphArea:F1}", $"{pctAsph:F1} %"],
                ["3", "Тротуары, мощение и отмостка", $"{pavArea:F1}", $"{pctPav:F1} %"],
                ["4", "Озеленение (газоны, насаждения)", $"{greenArea:F1}", $"{pctGreen:F1} %"],
                ["", "Итого по участку:", $"{totalArea:F1}", "100.0 %"],
                ["", $"Коэффициент застройки: Kз = {kz:F2}", $"Kоз = {koz:F2}", "Норма"]
            ];

            double t0HeightMm = DrawSheetTable(
                doc, sheet, colLeftX, currentTopY,
                t0Cols, t0Header, t0Rows,
                minHeaderHeightMm: 7.5, minRowHeightMm: 5.2,
                fontHeaderTypeId, fontCellTypeId, fontBoldCellTypeId);

            currentTopY -= (t0HeightMm + 7.0) * MmToFeet;

            // =========================================================================
            // 1. ТАБЛИЦА 1: "Экспликация зданий и сооружений" (ГОСТ 21.508-2020, Форма 2)
            // =========================================================================
            DrawSheetText(doc, sheet,
                new XYZ(colLeftX + ((colWidthMm / 2.0) * MmToFeet), currentTopY, 0.0),
                "Экспликация зданий и сооружений",
                fontHeaderTypeId, HorizontalTextAlignment.Center);

            currentTopY -= 5.0 * MmToFeet;

            double[] t1Cols = [14.0, 76.0, 50.0];
            string[] t1Header = ["Номер\nна плане", "Наименование", "Примечание"];

            double t1HeightMm = DrawSheetTable(
                doc, sheet, colLeftX, currentTopY,
                t1Cols, t1Header, data.Table1Rows,
                minHeaderHeightMm: 7.5, minRowHeightMm: 5.6,
                fontHeaderTypeId, fontCellTypeId, fontBoldCellTypeId);

            currentTopY -= (t1HeightMm + 8.0) * MmToFeet;

            // =========================================================================
            // 2. ТАБЛИЦА 2: "Расчет размеров площадок" (по нормам СП)
            // =========================================================================
            DrawSheetText(doc, sheet,
                new XYZ(colLeftX + ((colWidthMm / 2.0) * MmToFeet), currentTopY, 0.0),
                "Расчет размеров площадок",
                fontHeaderTypeId, HorizontalTextAlignment.Center);

            currentTopY -= 5.0 * MmToFeet;

            double[] t2Cols = [48.0, 22.0, 22.0, 23.0, 25.0];
            string[] t2Header =
            [
                "Наименование",
                "Удельный\nразмер,\nм²/ед.",
                "Расчетная\nединица,\nчел./мест",
                "Требуемая\nплощадь,\nм²",
                "Проектируемая\nплощадь,\nм²"
            ];

            double t2HeightMm = DrawSheetTable(
                doc, sheet, colLeftX, currentTopY,
                t2Cols, t2Header, data.Table2Rows,
                minHeaderHeightMm: 11.5, minRowHeightMm: 5.6,
                fontHeaderTypeId, fontCellTypeId, fontBoldCellTypeId);

            currentTopY -= (t2HeightMm + 8.0) * MmToFeet;

            // =========================================================================
            // 3. БЛОК: Расчет вместимости / населения
            // =========================================================================
            DrawSheetText(doc, sheet,
                new XYZ(colLeftX + ((colWidthMm / 2.0) * MmToFeet), currentTopY, 0.0),
                data.Calc1Header,
                fontBoldCellTypeId, HorizontalTextAlignment.Center);
            currentTopY -= 3.8 * MmToFeet;

            DrawSheetText(doc, sheet,
                new XYZ(colLeftX + ((colWidthMm / 2.0) * MmToFeet), currentTopY, 0.0),
                data.Calc1Formula,
                fontCalcFormulaTypeId, HorizontalTextAlignment.Center);
            currentTopY -= 3.4 * MmToFeet;

            DrawSheetText(doc, sheet,
                new XYZ(colLeftX + (1.5 * MmToFeet), currentTopY, 0.0),
                data.Calc1Desc,
                fontCalcTextTypeId, HorizontalTextAlignment.Left, colWidthMm * MmToFeet);
            currentTopY -= 4.2 * MmToFeet;

            DrawSheetText(doc, sheet,
                new XYZ(colLeftX + ((colWidthMm / 2.0) * MmToFeet), currentTopY, 0.0),
                data.Calc1Result,
                fontCalcFormulaTypeId, HorizontalTextAlignment.Center);
            currentTopY -= 6.0 * MmToFeet;

            // =========================================================================
            // 4. БЛОК: Расчет ТБО
            // =========================================================================
            DrawSheetText(doc, sheet,
                new XYZ(colLeftX + ((colWidthMm / 2.0) * MmToFeet), currentTopY, 0.0),
                data.Calc2Header,
                fontBoldCellTypeId, HorizontalTextAlignment.Center);
            currentTopY -= 3.8 * MmToFeet;

            DrawSheetText(doc, sheet,
                new XYZ(colLeftX + ((colWidthMm / 2.0) * MmToFeet), currentTopY, 0.0),
                data.Calc2Formula,
                fontCalcFormulaTypeId, HorizontalTextAlignment.Center);
            currentTopY -= 3.4 * MmToFeet;

            DrawSheetText(doc, sheet,
                new XYZ(colLeftX + (1.5 * MmToFeet), currentTopY, 0.0),
                data.Calc2Result,
                fontCalcTextTypeId, HorizontalTextAlignment.Left, colWidthMm * MmToFeet);
        }

        private static void DrawArchitecturalWindRoseOnSheet(
            Document doc,
            ViewSheet sheet,
            double originX,
            double originY)
        {
            EnsureSheetSketchPlane(doc, sheet);

            double cx = originX + (38.0 * MmToFeet);
            double cy = originY + ((SheetHeightMm - MarginTopMm - 38.0) * MmToFeet);

            double rNorth = 22.0 * MmToFeet;
            double rSouth = 16.0 * MmToFeet;
            double rEast = 18.0 * MmToFeet;
            double rWest = 18.0 * MmToFeet;
            double rDiag = 12.0 * MmToFeet;

            // 1. Координатные оси розы ветров
            DrawSheetLine(doc, sheet, new XYZ(cx, cy - rSouth - (4.0 * MmToFeet), 0.0), new XYZ(cx, cy + rNorth, 0.0));
            DrawSheetLine(doc, sheet, new XYZ(cx - rWest - (3.0 * MmToFeet), cy, 0.0), new XYZ(cx + rEast + (3.0 * MmToFeet), cy, 0.0));

            // Диагональные засечки
            double dHalf = rDiag * 0.7071;
            DrawSheetLine(doc, sheet, new XYZ(cx - dHalf, cy - dHalf, 0.0), new XYZ(cx + dHalf, cy + dHalf, 0.0));
            DrawSheetLine(doc, sheet, new XYZ(cx - dHalf, cy + dHalf, 0.0), new XYZ(cx + dHalf, cy - dHalf, 0.0));

            // 2. Многоугольник повторяемости розы ветров (по СПДС / ГОСТ)
            XYZ ptN = new XYZ(cx, cy + (19.0 * MmToFeet), 0.0);
            XYZ ptNE = new XYZ(cx + (11.0 * MmToFeet * 0.7071), cy + (11.0 * MmToFeet * 0.7071), 0.0);
            XYZ ptE = new XYZ(cx + (15.0 * MmToFeet), cy, 0.0);
            XYZ ptSE = new XYZ(cx + (9.0 * MmToFeet * 0.7071), cy - (9.0 * MmToFeet * 0.7071), 0.0);
            XYZ ptS = new XYZ(cx, cy - (8.0 * MmToFeet), 0.0);
            XYZ ptSW = new XYZ(cx - (12.0 * MmToFeet * 0.7071), cy - (12.0 * MmToFeet * 0.7071), 0.0);
            XYZ ptW = new XYZ(cx - (16.0 * MmToFeet), cy, 0.0);
            XYZ ptNW = new XYZ(cx - (14.0 * MmToFeet * 0.7071), cy + (14.0 * MmToFeet * 0.7071), 0.0);

            DrawSheetLine(doc, sheet, ptN, ptNE);
            DrawSheetLine(doc, sheet, ptNE, ptE);
            DrawSheetLine(doc, sheet, ptE, ptSE);
            DrawSheetLine(doc, sheet, ptSE, ptS);
            DrawSheetLine(doc, sheet, ptS, ptSW);
            DrawSheetLine(doc, sheet, ptSW, ptW);
            DrawSheetLine(doc, sheet, ptW, ptNW);
            DrawSheetLine(doc, sheet, ptNW, ptN);

            // 3. Стрелка-указатель направления на север
            double arrowBaseHalfW = 2.5 * MmToFeet;
            XYZ arrowLeft = new XYZ(cx - arrowBaseHalfW, cy + (12.0 * MmToFeet), 0.0);
            XYZ arrowRight = new XYZ(cx + arrowBaseHalfW, cy + (12.0 * MmToFeet), 0.0);
            XYZ arrowTip = new XYZ(cx, cy + rNorth + (3.0 * MmToFeet), 0.0);

            DrawSheetLine(doc, sheet, arrowLeft, arrowTip);
            DrawSheetLine(doc, sheet, arrowRight, arrowTip);
            DrawSheetLine(doc, sheet, arrowLeft, new XYZ(cx, cy, 0.0));
            DrawSheetLine(doc, sheet, arrowRight, new XYZ(cx, cy, 0.0));

            // 4. Буква "С" (Север)
            ElementId northTextTypeId = GetOrCreateGostTextNoteTypeId(doc, 5.0, bold: true, suffix: "Север_5мм");
            XYZ letterPt = new XYZ(cx, cy + rNorth + (6.0 * MmToFeet), 0.0);
            DrawSheetText(doc, sheet, letterPt, "С", northTextTypeId, HorizontalTextAlignment.Center);
        }

        private static Floor? TryCreateFloorZone(
            Document doc,
            IList<CurveLoop> loops,
            ElementId floorTypeId,
            ElementId levelId,
            string comment,
            string mark,
            double offsetFeet)
        {
            try
            {
                Floor floor = Floor.Create(doc, loops, floorTypeId, levelId);
                SetZoneMetadata(floor, comment, mark);

                Parameter? offsetParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (offsetParam is { IsReadOnly: false })
                {
                    offsetParam.Set(offsetFeet);
                }
                return floor;
            }
            catch
            {
                // Пропускаем при конфликте геометрии
                return null;
            }
        }

        private static void SetZoneMetadata(Element element, string comment, string mark)
        {
            Parameter? commentParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (commentParam is { IsReadOnly: false })
            {
                commentParam.Set(comment);
            }

            Parameter? markParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (markParam is { IsReadOnly: false })
            {
                markParam.Set(mark);
            }
        }

        private static double CalculateCurveLoopAreaM2(CurveLoop loop)
        {
            try
            {
                CurveLoop copy = CurveLoop.CreateViaCopy(loop);
                if (!copy.IsCounterclockwise(XYZ.BasisZ))
                {
                    copy.Flip();
                }

                Solid extrusion = GeometryCreationUtilities.CreateExtrusionGeometry([copy], XYZ.BasisZ, 1.0);
                return UnitUtils.ConvertFromInternalUnits(extrusion.Volume, UnitTypeId.SquareMeters);
            }
            catch
            {
                List<XYZ> pts = GetLoopVertices(loop);
                double twiceAreaSqFt = 0.0;
                for (int i = 0; i < pts.Count; i++)
                {
                    XYZ a = pts[i];
                    XYZ b = pts[(i + 1) % pts.Count];
                    twiceAreaSqFt += (a.X * b.Y) - (b.X * a.Y);
                }
                return UnitUtils.ConvertFromInternalUnits(Math.Abs(twiceAreaSqFt) * 0.5, UnitTypeId.SquareMeters);
            }
        }

        // =========================================================================
        // 5. ПОДГОТОВКА 2D-ПЛАНА И 3D-ВИДА ГЕНПЛАНА, ТАБЛИЦЫ ТЭП И ЛИСТА "ГП-1"
        // =========================================================================

        private static ViewPlan GetOrCreateSitePlanView(Document doc, Level firstLevel, CurveLoop siteBoundaryLoop)
        {
            const string siteViewName = "ГП_3D Генеральный план (План)";

            ViewPlan? siteView = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate
                                     && v.Name.Equals(siteViewName, StringComparison.OrdinalIgnoreCase));

            if (siteView != null)
            {
                Viewport? oldVp = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .FirstOrDefault(vp => vp.ViewId == siteView.Id);
                if (oldVp != null)
                {
                    try { doc.Delete(oldVp.Id); doc.Regenerate(); } catch { }
                }
            }

            if (siteView == null)
            {
                ElementId viewFamilyTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily is ViewFamily.FloorPlan or ViewFamily.AreaPlan)?.Id
                    ?? throw new InvalidOperationException("Не найден тип вида для создания плана (ViewFamilyType).");

                siteView = ViewPlan.Create(doc, viewFamilyTypeId, firstLevel.Id);
                AssignUniqueViewName(doc, siteView, siteViewName);
            }

            // Отключаем шаблон вида, если он блокирует тонированный стиль или секущий диапазон
            siteView.ViewTemplateId = ElementId.InvalidElementId;
            siteView.DisplayStyle = DisplayStyle.ShadingWithEdges;
            siteView.DetailLevel = ViewDetailLevel.Fine;

            // Расширяем секущий диапазон (ViewRange) вниз на -3.5 м и вверх на +8.0 м,
            // чтобы гарантированно видеть и плиты покрытий, и 3D-деревья, и здание
            try
            {
                PlanViewRange viewRange = siteView.GetViewRange();
                viewRange.SetOffset(PlanViewPlane.TopClipPlane, 8000.0 * MmToFeet);
                viewRange.SetOffset(PlanViewPlane.CutPlane, 2000.0 * MmToFeet);
                viewRange.SetOffset(PlanViewPlane.BottomClipPlane, -2500.0 * MmToFeet);
                viewRange.SetOffset(PlanViewPlane.ViewDepthPlane, -3500.0 * MmToFeet);
                siteView.SetViewRange(viewRange);
            }
            catch
            {
                // Продолжаем работу
            }

            EnsureCategoryVisible(siteView, BuiltInCategory.OST_Site);
            EnsureCategoryVisible(siteView, BuiltInCategory.OST_Planting);
            EnsureCategoryVisible(siteView, BuiltInCategory.OST_Walls);
            EnsureCategoryVisible(siteView, BuiltInCategory.OST_Floors);
            EnsureCategoryVisible(siteView, BuiltInCategory.OST_Toposolid);

            List<XYZ> pts = GetLoopVertices(siteBoundaryLoop);
            double pad = 2000.0 * MmToFeet;
            double minX = pts.Min(p => p.X) - pad;
            double maxX = pts.Max(p => p.X) + pad;
            double minY = pts.Min(p => p.Y) - pad;
            double maxY = pts.Max(p => p.Y) + pad;

            siteView.CropBoxActive = true;
            siteView.CropBoxVisible = false;

            BoundingBoxXYZ cropBox = siteView.CropBox;
            cropBox.Min = new XYZ(minX, minY, cropBox.Min.Z);
            cropBox.Max = new XYZ(maxX, maxY, cropBox.Max.Z);
            siteView.CropBox = cropBox;

            return siteView;
        }

        /// <summary>
        /// Создает 3D-изометрический вид генерального плана ("ГП_3D Модель генплана") в тонированном режиме.
        /// </summary>
        private static View3D? GetOrCreateSite3DView(Document doc, CurveLoop siteBoundaryLoop, double baseZ)
        {
            try
            {
                const string view3DName = "ГП_3D Модель генплана";

                HashSet<ElementId> placedViewIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .Select(vp => vp.ViewId)
                    .ToHashSet();

                View3D? view3D = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate
                                         && !v.IsPerspective
                                         && v.Name.Equals(view3DName, StringComparison.OrdinalIgnoreCase)
                                         && !placedViewIds.Contains(v.Id));

                if (view3D == null)
                {
                    ElementId? view3DTypeId = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional)?.Id;

                    if (view3DTypeId == null)
                    {
                        return null;
                    }

                    view3D = View3D.CreateIsometric(doc, view3DTypeId);
                    AssignUniqueViewName(doc, view3D, view3DName);
                }

                view3D.ViewTemplateId = ElementId.InvalidElementId;
                view3D.DisplayStyle = DisplayStyle.ShadingWithEdges;
                view3D.DetailLevel = ViewDetailLevel.Fine;

                EnsureCategoryVisible(view3D, BuiltInCategory.OST_Site);
                EnsureCategoryVisible(view3D, BuiltInCategory.OST_Planting);
                EnsureCategoryVisible(view3D, BuiltInCategory.OST_Walls);
                EnsureCategoryVisible(view3D, BuiltInCategory.OST_Floors);

                // Ограничиваем 3D-вид рамкой SectionBox вокруг участка
                List<XYZ> pts = GetLoopVertices(siteBoundaryLoop);
                double pad = 2000.0 * MmToFeet;
                BoundingBoxXYZ sectionBox = new BoundingBoxXYZ
                {
                    Min = new XYZ(pts.Min(p => p.X) - pad, pts.Min(p => p.Y) - pad, baseZ - (800.0 * MmToFeet)),
                    Max = new XYZ(pts.Max(p => p.X) + pad, pts.Max(p => p.Y) + pad, baseZ + (25000.0 * MmToFeet))
                };
                view3D.SetSectionBox(sectionBox);

                return view3D;
            }
            catch
            {
                return null;
            }
        }

        private static ViewSchedule CreateTerritoryBalanceSchedule(
            Document doc,
            double buildingAreaM2,
            double blindAreaM2,
            double roadsAndParkingAreaM2,
            double greenAreaM2,
            double totalSiteAreaM2,
            int parkingSpotsCount)
        {
            const string scheduleName = "Экспликация и баланс территории ГП";

            ViewSchedule? existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(s => !s.IsTemplate && s.Name.Equals(scheduleName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                try
                {
                    doc.Delete(existing.Id);
                    doc.Regenerate();
                }
                catch
                {
                    // Создадим с уникальным именем
                }
            }

            ElementId floorsCatId = new ElementId(BuiltInCategory.OST_Floors);
            ViewSchedule schedule = ViewSchedule.CreateSchedule(doc, floorsCatId);
            AssignUniqueViewName(doc, schedule, scheduleName);

            ScheduleDefinition definition = schedule.Definition;
            IList<SchedulableField> schedulableFields = definition.GetSchedulableFields();

            ScheduleField? markField = AddScheduleField(definition, schedulableFields, BuiltInParameter.ALL_MODEL_MARK, "№", 12.0);
            ScheduleField? commentField = AddScheduleField(definition, schedulableFields, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Элемент 3D-генплана / покрытие", 88.0);
            ScheduleField? areaField = AddScheduleField(definition, schedulableFields, BuiltInParameter.HOST_AREA_COMPUTED, "Площадь, м²", 32.0);

            if (areaField != null)
            {
                areaField.DisplayType = ScheduleFieldDisplayType.Totals;
            }

            if (commentField != null)
            {
                ScheduleFilter filter = new ScheduleFilter(
                    commentField.FieldId,
                    ScheduleFilterType.BeginsWith,
                    "ГП:");
                definition.AddFilter(filter);
            }

            if (markField != null)
            {
                ScheduleSortGroupField sortField = new ScheduleSortGroupField(markField.FieldId);
                definition.AddSortGroupField(sortField);
            }

            definition.ShowGrandTotal = true;
            definition.ShowGrandTotalTitle = true;
            definition.GrandTotalTitle = string.Format(
                CultureInfo.InvariantCulture,
                "ТЭП: Застр. {0:F1} м² | Трот. {1:F1} м² | Дор.+Парк.({2} м/м) {3:F1} м² | Газон {4:F1} м² | Участок {5:F1} м²",
                buildingAreaM2,
                blindAreaM2,
                parkingSpotsCount,
                roadsAndParkingAreaM2,
                greenAreaM2,
                totalSiteAreaM2);

            doc.Regenerate();
            return schedule;
        }

        private static ScheduleField? AddScheduleField(
            ScheduleDefinition definition,
            IList<SchedulableField> schedulableFields,
            BuiltInParameter parameter,
            string columnHeader,
            double widthMm)
        {
            ElementId paramId = new ElementId(parameter);
            SchedulableField? match = schedulableFields.FirstOrDefault(f => f.ParameterId == paramId);
            if (match == null)
            {
                return null;
            }

            ScheduleField field = definition.AddField(match);
            field.ColumnHeading = columnHeader;
            field.SheetColumnWidth = widthMm * MmToFeet;
            return field;
        }

        private static void EnsureCategoryVisible(View view, BuiltInCategory category)
        {
            try
            {
                ElementId catId = new ElementId(category);
                if (view.CanCategoryBeHidden(catId) && view.GetCategoryHidden(catId))
                {
                    view.SetCategoryHidden(catId, false);
                }
            }
            catch
            {
                // Игнорируем, если видимостью управляет шаблон
            }
        }

        private static int ApplyOptimalStandardScale(View view, double usableWidthMm, double usableHeightMm)
        {
            double viewWidthMm = 0.0;
            double viewHeightMm = 0.0;

            if (view.CropBoxActive && view.CropBox != null)
            {
                BoundingBoxXYZ cropBox = view.CropBox;
                viewWidthMm = UnitUtils.ConvertFromInternalUnits(
                    Math.Abs(cropBox.Max.X - cropBox.Min.X),
                    UnitTypeId.Millimeters);
                viewHeightMm = UnitUtils.ConvertFromInternalUnits(
                    Math.Abs(cropBox.Max.Y - cropBox.Min.Y),
                    UnitTypeId.Millimeters);
            }
            else if (view.Outline != null && view.Scale > 0)
            {
                BoundingBoxUV outline = view.Outline;
                viewWidthMm = UnitUtils.ConvertFromInternalUnits(
                    Math.Abs(outline.Max.U - outline.Min.U),
                    UnitTypeId.Millimeters) * view.Scale;
                viewHeightMm = UnitUtils.ConvertFromInternalUnits(
                    Math.Abs(outline.Max.V - outline.Min.V),
                    UnitTypeId.Millimeters) * view.Scale;
            }

            if (viewWidthMm <= 1e-3 || viewHeightMm <= 1e-3)
            {
                return view.Scale > 0 ? view.Scale : 200;
            }

            double requiredScale = Math.Max(viewWidthMm / usableWidthMm, viewHeightMm / usableHeightMm);
            int selectedScale = StandardScales[^1];

            foreach (int scale in StandardScales)
            {
                if (scale >= requiredScale)
                {
                    selectedScale = scale;
                    break;
                }
            }

            Parameter? scaleParam = view.get_Parameter(BuiltInParameter.VIEW_SCALE);
            if (scaleParam is { IsReadOnly: false } && View.IsValidViewScale(selectedScale))
            {
                view.Scale = selectedScale;
            }

            return view.Scale;
        }

        private static double PlaceScheduleAboveStamp(
            Document doc,
            ViewSheet sheet,
            ViewSchedule schedule,
            double originX,
            double originY,
            out double finalTopYFt)
        {
            finalTopYFt = originY + ((MarginBottomFrameMm + StampHeightMm + 60.0) * MmToFeet);
            try
            {
                ScheduleSheetInstance instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, XYZ.Zero);
                doc.Regenerate();

                BoundingBoxXYZ? box = instance.get_BoundingBox(sheet);
                if (box == null)
                {
                    return 132.0;
                }

                double widthFt = Math.Abs(box.Max.X - box.Min.X);
                double heightFt = Math.Abs(box.Max.Y - box.Min.Y);
                double widthMm = UnitUtils.ConvertFromInternalUnits(widthFt, UnitTypeId.Millimeters);

                double targetRightX = originX + ((SheetWidthMm - MarginRightMm - 2.0) * MmToFeet);
                double targetBottomY = originY + ((MarginBottomFrameMm + StampHeightMm + 2.0) * MmToFeet);

                XYZ moveVector = new XYZ(targetRightX - box.Max.X, targetBottomY - box.Min.Y, 0.0);
                ElementTransformUtils.MoveElement(doc, instance.Id, moveVector);

                finalTopYFt = targetBottomY + heightFt;
                return widthMm;
            }
            catch
            {
                return 0.0;
            }
        }

        // =========================================================================
        // РАБОТА С ВСТРОЕННЫМ ЛИСТОМ "Лист (Форма3).rfa" (ТИП "А3_А")
        // =========================================================================

        private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(
                Family sharedFamily,
                bool familyInUse,
                out FamilySource source,
                out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        private static FamilySymbol? FindOrLoadTitleBlockSymbol(Document doc)
        {
            FamilySymbol? symbol = FindCustomTitleBlockInDoc(doc);
            if (symbol != null)
            {
                return symbol;
            }

            string? rfaPath = ResolveCustomTitleBlockRfaPath();
            if (!string.IsNullOrEmpty(rfaPath) && File.Exists(rfaPath))
            {
                try
                {
                    doc.LoadFamily(rfaPath, new OverwriteFamilyLoadOptions(), out _);
                    doc.Regenerate();
                }
                catch
                {
                    // Продолжаем поиск
                }

                symbol = FindCustomTitleBlockInDoc(doc);
                if (symbol != null)
                {
                    return symbol;
                }
            }

            List<FamilySymbol> allTitleBlocks = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .ToList();

            return allTitleBlocks.FirstOrDefault(tb =>
                       tb.Name.Contains("А3", StringComparison.OrdinalIgnoreCase) ||
                       tb.Name.Contains("A3", StringComparison.OrdinalIgnoreCase))
                   ?? allTitleBlocks.FirstOrDefault();
        }

        private static FamilySymbol? FindCustomTitleBlockInDoc(Document doc)
        {
            List<FamilySymbol> symbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .Where(tb => tb.FamilyName.Equals(CustomTitleBlockFamilyName, StringComparison.OrdinalIgnoreCase) ||
                             tb.FamilyName.Contains("Форма3", StringComparison.OrdinalIgnoreCase) ||
                             tb.FamilyName.Contains("Форма 3", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (symbols.Count == 0)
            {
                return null;
            }

            return symbols.FirstOrDefault(s => s.Name.Equals(CustomTitleBlockTypeName, StringComparison.OrdinalIgnoreCase))
                   ?? symbols.FirstOrDefault(s => s.Name.Contains("А3", StringComparison.OrdinalIgnoreCase))
                   ?? symbols[0];
        }

        private static string? ResolveCustomTitleBlockRfaPath()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string? assemblyDir = Path.GetDirectoryName(assembly.Location);

            if (!string.IsNullOrEmpty(assemblyDir))
            {
                string localPath = Path.Combine(assemblyDir, CustomTitleBlockFileName);
                if (File.Exists(localPath))
                {
                    return localPath;
                }
            }

            using Stream? resourceStream = assembly.GetManifestResourceStream(EmbeddedTitleBlockResourceName);
            if (resourceStream == null)
            {
                return null;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), CustomTitleBlockFileName);
            using FileStream fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            resourceStream.CopyTo(fileStream);
            return tempPath;
        }

        private static void ConfigureTitleBlockInstanceA3(Document doc, ViewSheet sheet)
        {
            Element? tbInstance = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            if (tbInstance == null)
            {
                return;
            }

            TrySetIntParameter(tbInstance, "Формат А", 3);
            TrySetIntParameter(tbInstance, "Кратность", 1);
            TrySetIntParameter(tbInstance, "Книжная ориентация", 0);
            TrySetIntParameter(tbInstance, "Альбомная ориентация", 1);
            TrySetIntParameter(tbInstance, "Нестандартный размер", 0);
        }

        private static void TrySetIntParameter(Element element, string paramName, int value)
        {
            Parameter? param = element.LookupParameter(paramName);
            if (param is { IsReadOnly: false, StorageType: StorageType.Integer })
            {
                try
                {
                    param.Set(value);
                }
                catch
                {
                    // Игнорируем параметры под управлением формул типа
                }
            }
        }

        private static (double OriginX, double OriginY) GetSheetOrigin(Document doc, ViewSheet sheet)
        {
            return SheetLayoutEngine.GetSheetOriginFt(doc, sheet);
        }

        private static void AssignUniqueSheetNumber(Document doc, ViewSheet sheet, string desiredNumber)
        {
            HashSet<string> usedNumbers = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => s.Id != sheet.Id)
                .Select(s => s.SheetNumber)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!usedNumbers.Contains(desiredNumber))
            {
                sheet.SheetNumber = desiredNumber;
                return;
            }

            int index = 2;
            while (usedNumbers.Contains($"{desiredNumber} ({index})"))
            {
                index++;
            }

            sheet.SheetNumber = $"{desiredNumber} ({index})";
        }

        private static void AssignUniqueViewName(Document doc, View view, string desiredName)
        {
            HashSet<string> usedNames = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.Id != view.Id)
                .Select(v => v.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!usedNames.Contains(desiredName))
            {
                view.Name = desiredName;
                return;
            }

            int index = 2;
            while (usedNames.Contains($"{desiredName} ({index})"))
            {
                index++;
            }

            view.Name = $"{desiredName} ({index})";
        }
    }
}
