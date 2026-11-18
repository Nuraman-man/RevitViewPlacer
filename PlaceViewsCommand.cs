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
    public class PlaceViewsCommand : IExternalCommand
    {
        // Коэффициент перевода миллиметров во внутренние единицы Revit (футы)
        private const double MmToFeet = 1.0 / 304.8;

        // Встроенное семейство основной надписи пользователя ("Лист (Форма3).rfa", тип "А3_А")
        private const string CustomTitleBlockFamilyName = "Лист (Форма3)";
        private const string CustomTitleBlockTypeName = "А3_А";
        private const string CustomTitleBlockFileName = "Лист (Форма3).rfa";
        private const string EmbeddedTitleBlockResourceName = "RevitViewPlacer.TitleBlockA3.rfa";

        // Префикс имени для архитектурных видов
        private const string RequiredViewPrefix = "АР_";

        // Стандартный ряд масштабов Revit (1:N)
        private static readonly int[] StandardScales =
        [
            1, 2, 5, 10, 20, 25, 50, 100, 200, 500, 1000, 2000, 5000
        ];

        // Габариты листа формата А3 (альбомная ориентация, ГОСТ 21.1101) в миллиметрах
        private const double SheetWidthMm = 420.0;
        private const double SheetHeightMm = 297.0;

        // Отступы рамки и основной надписи (штампа) в миллиметрах
        private const double MarginLeftMm = 20.0;        // Поле для подшивки слева
        private const double MarginRightMm = 5.0;        // Правое поле рамки
        private const double MarginTopMm = 5.0;          // Верхнее поле рамки
        private const double MarginBottomFrameMm = 5.0;  // Нижнее поле рамки
        private const double StampHeightMm = 55.0;       // Высота основной надписи (штампа) снизу

        // Величины смещений контуров генерального плана (в миллиметрах)
        private const double BlindAreaOffsetMm = 1500.0;     // Отмостка / пешеходная зона
        private const double RoadOffsetMm = 6000.0;          // Проезд / автодорога
        private const double SiteBoundaryOffsetMm = 15000.0; // Граница участка / благоустройства

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiApp = commandData.Application;
                UIDocument? uiDoc = uiApp.ActiveUIDocument;

                if (uiDoc?.Document == null)
                {
                    message = "Нет открытого документа проекта Revit.";
                    TaskDialog.Show("Авторазмещение и Генплан", message);
                    return Result.Failed;
                }

                Document doc = uiDoc.Document;

                if (doc.IsFamilyDocument)
                {
                    message = "Плагин предназначен для работы в документе проекта, а не в редакторе семейств.";
                    TaskDialog.Show("Авторазмещение и Генплан", message);
                    return Result.Failed;
                }

                // Предварительно приводим существующие координационные оси к ГОСТ (1, 2, 3... и А, Б, В...),
                // чтобы в окне выбора листов фасады сразу именовались по актуальным крайним осям
                GostGridManager.RenumberExistingGridsByGost(doc);

                // =========================================================================
                // ЭТАП 0. Запрос имени пользователя и реквизитов штампа по ГОСТ Р 21.1101
                // =========================================================================
                GostStampData? gostStamp = GostStampHelper.PromptUserForGostStampData(doc);
                if (gostStamp == null)
                {
                    return Result.Cancelled;
                }

                FamilySymbol? titleBlockSymbol;
                using (Transaction txLoadTb = new Transaction(doc, "Загрузка семейства листа Форма 3 (А3_А)"))
                {
                    SheetLayoutEngine.ConfigureSilentTransaction(txLoadTb);
                    txLoadTb.Start();
                    titleBlockSymbol = FindOrLoadCustomTitleBlockSymbol(doc);
                    txLoadTb.Commit();
                }

                if (titleBlockSymbol == null)
                {
                    message = "Не удалось загрузить или найти семейство основной надписи.";
                    TaskDialog.Show("Ошибка", message);
                    return Result.Failed;
                }

                // Вписываем имя пользователя по ГОСТ непосредственно в шаблонные строки семейства "Лист (Форма3)"
                if (titleBlockSymbol.Family != null)
                {
                    GostStampHelper.UpdateTitleBlockFamilyStampTexts(doc, titleBlockSymbol.Family, gostStamp);
                }

                // Вычисляем чистые габариты самого здания (без учета 50-метрового участка ГП)
                BoundingBoxXYZ? buildingBBox = SheetLayoutEngine.ComputeBuildingWorldBoundingBox(doc);

                // =========================================================================
                // ЭТАП 1. Генерация 3D-генплана (ГП-1), парковки, озеленения и ТЭП (по выбору пользователя)
                // (Выполняется перед подрезкой видов АР_, чтобы скрыть элементы ГП на видах здания)
                // =========================================================================
                GenerateDetailedSitePlanCommand.DetailedSitePlanSummary? siteSummary = null;

                if (gostStamp.IncludeSitePlanSheet)
                {
                    using (Transaction txGp = new Transaction(doc, "Генерация 3D-модели генплана ГП-1, парковки и ТЭП (СН РК)"))
                    {
                        SheetLayoutEngine.ConfigureSilentTransaction(txGp);
                        txGp.Start();

                        siteSummary = GenerateDetailedSitePlanCommand.ExecuteSitePlanPipeline(doc, gostStamp, gostStamp.CustomSitePlanConfig);

                        txGp.Commit();
                    }
                }
                else
                {
                    // Если пользователь снял галочку с ГП-1, а лист ГП-1 существовал ранее — удаляем лишний лист ГП-1
                    using (Transaction txDelGp = new Transaction(doc, "Удаление отключенного листа ГП-1"))
                    {
                        SheetLayoutEngine.ConfigureSilentTransaction(txDelGp);
                        txDelGp.Start();
                        ViewSheet? oldGp = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewSheet))
                            .Cast<ViewSheet>()
                            .FirstOrDefault(s => !s.IsTemplate && s.SheetNumber.Equals("ГП-1", StringComparison.OrdinalIgnoreCase));
                        if (oldGp != null)
                        {
                            try { doc.Delete(oldGp.Id); } catch { }
                        }
                        txDelGp.Commit();
                    }
                }

                // =========================================================================
                // ЭТАП 2. Комплексное создание и оформление всех выбранных листов по ГОСТ РК / СН РК
                // =========================================================================
                int placedArViewsCount = 0;
                int upgradedSheetsCount = 0;
                int removedExtraSheetsCount = 0;
                int placedRoomSchedulesCount = 0;
                int replacedRoomTagsCount = 0;
                int createdRoomTagsCount = 0;
                int renumberedRoomsCount = 0;
                List<string> scaleReportLines = [];

                using (Transaction txAr = new Transaction(doc, "Создание и оформление комплекта листов по ГОСТ РК и СН РК"))
                {
                    SheetLayoutEngine.ConfigureSilentTransaction(txAr);
                    txAr.Start();

                    titleBlockSymbol = FindOrLoadCustomTitleBlockSymbol(doc) ?? titleBlockSymbol;
                    if (!titleBlockSymbol.IsActive)
                    {
                        titleBlockSymbol.Activate();
                        doc.Regenerate();
                    }

                    // Санитизируем любые существующие спецификации помещений проекта (исключаем Not Placed / Not Enclosed)
                    CleanExistingRoomSchedules(doc);

                    // Приводим в порядок координационные оси проекта по ГОСТ (1, 2, 3... слева направо, А, Б, В... снизу вверх)
                    GostGridManager.RenumberExistingGridsByGost(doc);

                    // Формируем множество выбранных пользователем ElementId видов и словарь пользовательских масштабов
                    HashSet<ElementId> selectedViewIds = gostStamp.SelectedViewIds != null
                        ? gostStamp.SelectedViewIds.Select(idVal => new ElementId(idVal)).ToHashSet()
                        : [];

                    Dictionary<ElementId, int> customViewScales = [];
                    Dictionary<long, int> customScalesByLong = [];
                    if (gostStamp.CustomViewScales != null)
                    {
                        foreach (KeyValuePair<long, int> kv in gostStamp.CustomViewScales)
                        {
                            customViewScales[new ElementId(kv.Key)] = kv.Value;
                            customScalesByLong[kv.Key] = kv.Value;
                        }
                    }

                    // Автоматически создаем стандартные разрезы 1–1 и/или 2–2, если они были отмечены галочками в диалоге
                    if (gostStamp.CreateAutoSection1 || gostStamp.CreateAutoSection2)
                    {
                        List<ViewSection> allSections = SectionSheetBuilder.EnsureStandardSectionsExist(
                            doc,
                            buildingBBox,
                            gostStamp.CreateAutoSection1,
                            gostStamp.CreateAutoSection2);
                        doc.Regenerate();

                        foreach (ViewSection sec in allSections)
                        {
                            if (gostStamp.CreateAutoSection1 &&
                                System.Text.RegularExpressions.Regex.IsMatch(sec.Name, @"(?:1[-–—]1|1\b|Продольн)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            {
                                selectedViewIds.Add(sec.Id);
                                customViewScales[sec.Id] = gostStamp.AutoSection1Scale;
                                customScalesByLong[sec.Id.Value] = gostStamp.AutoSection1Scale;
                            }
                            if (gostStamp.CreateAutoSection2 &&
                                System.Text.RegularExpressions.Regex.IsMatch(sec.Name, @"(?:2[-–—]2|2\b|Поперечн)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            {
                                selectedViewIds.Add(sec.Id);
                                customViewScales[sec.Id] = gostStamp.AutoSection2Scale;
                                customScalesByLong[sec.Id.Value] = gostStamp.AutoSection2Scale;
                            }
                        }
                    }

                    // Автоматически создаем 4 фасада по ГОСТ (южный, северный, восточный, западный), если они были выбраны
                    if (gostStamp.CreateAutoElevSouth || gostStamp.CreateAutoElevNorth ||
                        gostStamp.CreateAutoElevEast || gostStamp.CreateAutoElevWest)
                    {
                        List<ViewSection> allElevations = ElevationSheetBuilder.EnsureStandardElevationsExist(
                            doc,
                            buildingBBox,
                            gostStamp.CreateAutoElevSouth,
                            gostStamp.CreateAutoElevNorth,
                            gostStamp.CreateAutoElevEast,
                            gostStamp.CreateAutoElevWest);
                        doc.Regenerate();

                        (string nStart, string nEnd, string lStart, string lEnd) = SheetLayoutEngine.GetExtremeGridLabels(doc);

                        foreach (ViewSection elev in allElevations)
                        {
                            bool isSouth = ElevationSheetBuilder.IsElevationMatchingSide(elev, "SOUTH", nStart, nEnd, lStart, lEnd);
                            bool isNorth = ElevationSheetBuilder.IsElevationMatchingSide(elev, "NORTH", nStart, nEnd, lStart, lEnd);
                            bool isEast = ElevationSheetBuilder.IsElevationMatchingSide(elev, "EAST", nStart, nEnd, lStart, lEnd);
                            bool isWest = ElevationSheetBuilder.IsElevationMatchingSide(elev, "WEST", nStart, nEnd, lStart, lEnd);

                            if (isSouth && gostStamp.CreateAutoElevSouth)
                            {
                                selectedViewIds.Add(elev.Id);
                                customViewScales[elev.Id] = gostStamp.AutoElevSouthScale;
                                customScalesByLong[elev.Id.Value] = gostStamp.AutoElevSouthScale;
                            }
                            else if (isNorth && gostStamp.CreateAutoElevNorth)
                            {
                                selectedViewIds.Add(elev.Id);
                                customViewScales[elev.Id] = gostStamp.AutoElevNorthScale;
                                customScalesByLong[elev.Id.Value] = gostStamp.AutoElevNorthScale;
                            }
                            else if (isEast && gostStamp.CreateAutoElevEast)
                            {
                                selectedViewIds.Add(elev.Id);
                                customViewScales[elev.Id] = gostStamp.AutoElevEastScale;
                                customScalesByLong[elev.Id.Value] = gostStamp.AutoElevEastScale;
                            }
                            else if (isWest && gostStamp.CreateAutoElevWest)
                            {
                                selectedViewIds.Add(elev.Id);
                                customViewScales[elev.Id] = gostStamp.AutoElevWestScale;
                                customScalesByLong[elev.Id.Value] = gostStamp.AutoElevWestScale;
                            }
                        }
                    }

                    // Удаляем существующие архитектурные листы, виды которых пользователь снял галочкой ("убрать лишние")
                    removedExtraSheetsCount = RemoveUnselectedExistingArSheets(
                        doc,
                        selectedViewIds,
                        gostStamp);

                    // Собираем все выбранные виды и сортируем их строго по ГОСТ 21.501-93 / СН РК:
                    // 4. Планы этажей по порядку отметок уровней снизу вверх (1 этаж 0.000, 2 этаж +3.300...)
                    // 5. Разрезы (Разрез 1-1, Разрез 2-2...)
                    // 6. Фасады (Фасад 1-4, 4-1, А-В, В-А...)
                    // Дополнительные виды
                    List<View> allSelectedViews = selectedViewIds
                        .Select(id => doc.GetElement(id) as View)
                        .Where(v => v != null && !v.IsTemplate && v.CanBePrinted)
                        .Cast<View>()
                        .ToList();

                    List<View> planViews = allSelectedViews
                        .Where(v => v.ViewType is ViewType.FloorPlan or ViewType.AreaPlan or ViewType.EngineeringPlan or ViewType.CeilingPlan)
                        .OrderBy(v => v.GenLevel?.Elevation ?? 0.0)
                        .ThenBy(v => v.Name ?? string.Empty)
                        .ToList();

                    List<View> sectionViews = allSelectedViews
                        .Where(v => v.ViewType == ViewType.Section)
                        .OrderBy(v => v.Name ?? string.Empty)
                        .ToList();

                    List<View> elevationViews = allSelectedViews
                        .Where(v => v.ViewType == ViewType.Elevation)
                        .OrderBy(v => v.Name ?? string.Empty)
                        .ToList();

                    List<View> otherViews = allSelectedViews
                        .Where(v => !planViews.Contains(v) && !sectionViews.Contains(v) && !elevationViews.Contains(v))
                        .OrderBy(v => v.Name ?? string.Empty)
                        .ToList();

                    List<View> orderedModelViews = [..planViews, ..sectionViews, ..elevationViews, ..otherViews];

                    // Собираем карту уже существующих листов для видов в проекте
                    Dictionary<ElementId, ViewSheet> viewToCurrentSheet = [];
                    foreach (ViewSheet s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate))
                    {
                        foreach (ElementId vpId in s.GetAllViewports())
                        {
                            if (doc.GetElement(vpId) is Viewport vp && vp.ViewId != ElementId.InvalidElementId)
                            {
                                viewToCurrentSheet[vp.ViewId] = s;
                            }
                        }
                    }

                    // Временно освобождаем номера листов АР-* и ГП-*, чтобы присвоить сквозную нумерацию без конфликтов
                    List<ViewSheet> allArSheetsForRenumber = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsTemplate && (s.SheetNumber.StartsWith("АР-", StringComparison.OrdinalIgnoreCase) || s.SheetNumber.Equals("ГП-1", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    int tmpIdx = 1;
                    foreach (ViewSheet s in allArSheetsForRenumber)
                    {
                        try { s.SheetNumber = $"TMP-AR-{tmpIdx++}"; } catch { }
                    }

                    // Принцип 1 вид = 1 отдельный лист по ГОСТ 21.501 / СН РК:
                    // Каждый выбранный вид гарантированно получает собственный выделенный лист формата А3.
                    Dictionary<ElementId, ViewSheet> viewToAssignedSheet = [];
                    HashSet<ElementId> claimedSheetIds = [];
                    HashSet<ElementId> existingSheetIds = [];

                    // Фаза 1: Если вид уже размещен на существующем листе и этот лист свободен — закрепляем его за данным видом
                    foreach (View view in orderedModelViews)
                    {
                        if (viewToCurrentSheet.TryGetValue(view.Id, out ViewSheet? sheet) && sheet != null)
                        {
                            if (!claimedSheetIds.Contains(sheet.Id))
                            {
                                viewToAssignedSheet[view.Id] = sheet;
                                claimedSheetIds.Add(sheet.Id);
                                existingSheetIds.Add(sheet.Id);
                            }
                        }
                    }

                    // Фаза 2: С занятых листов удаляем видовые экраны ВСЕХ ДРУГИХ видов
                    foreach (KeyValuePair<ElementId, ViewSheet> kvp in viewToAssignedSheet)
                    {
                        ElementId ownerViewId = kvp.Key;
                        ViewSheet sheet = kvp.Value;

                        List<ElementId> foreignVpIds = [];
                        foreach (ElementId vpId in sheet.GetAllViewports())
                        {
                            if (doc.GetElement(vpId) is Viewport vp && vp.ViewId != ownerViewId)
                            {
                                foreignVpIds.Add(vpId);
                            }
                        }
                        if (foreignVpIds.Count > 0)
                        {
                            try { doc.Delete(foreignVpIds); } catch { }
                        }
                    }
                    doc.Regenerate();

                    // Фаза 3: Для видов без листа создаем персональный новый лист
                    foreach (View view in orderedModelViews)
                    {
                        if (!viewToAssignedSheet.ContainsKey(view.Id))
                        {
                            RemoveViewFromAnySheet(doc, view.Id);
                            doc.Regenerate();

                            ViewSheet newSheet = ViewSheet.Create(doc, titleBlockSymbol.Id);
                            viewToAssignedSheet[view.Id] = newSheet;
                            claimedSheetIds.Add(newSheet.Id);
                        }
                    }
                    doc.Regenerate();

                    // Итоговый подсчет количества листов в альбоме для правильного ГОСТ-заполнения граф "Листов" и сортировки
                    int totalSheetsCount =
                        (gostStamp.IncludeCoverSheet ? 1 : 0) +
                        (gostStamp.IncludeGeneralDataSheet ? 1 : 0) +
                        (gostStamp.IncludeSituationPlanSheet ? 1 : 0) +
                        (gostStamp.IncludeSitePlanSheet ? 1 : 0) +
                        orderedModelViews.Count +
                        (gostStamp.IncludeRenderSheet ? 1 : 0);

                    string FormatSheetNumber(int seq) =>
                        totalSheetsCount >= 10 ? $"АР-{seq:D2}" : $"АР-{seq}";

                    // Автоматическое упорядочение нумерации помещений по ГОСТ (1, 2, 3...)
                    if (gostStamp.RenumberRoomsSequentially)
                    {
                        RoomRenumberResult renumResult = RoomRenumberEngine.RenumberAllPlacedRooms(doc, gostStamp.RoomNumberingMode);
                        renumberedRoomsCount = renumResult.RenumberedCount;
                        doc.Regenerate();
                    }

                    // Опциональная пакетная замена марок помещений на стандартное семейство ADSK
                    if (gostStamp.ReplaceRoomTagsWithAdsk)
                    {
                        RoomTagReplacementResult tagRes = RoomTagManager.ReplaceOrPlaceRoomTags(
                            doc,
                            planViews.Count > 0 ? planViews : null,
                            preferredTypeName: gostStamp.PreferredRoomTagType,
                            tagUntaggedRooms: true);

                        replacedRoomTagsCount = tagRes.ReplacedCount;
                        createdRoomTagsCount = tagRes.CreatedCount;
                        doc.Regenerate();
                    }

                    int currentSeq = 1;
                    List<KzGostSheetStyler.ArSheetCatalogEntry> sheetCatalog = [];
                    List<(ViewSheet Sheet, int SeqNum, string GostTitle)> processedArSheets = [];

                    // =========================================================================
                    // 1. Лист 1: Обложка (Титульный лист) альбома проектов по ГОСТ 21.101-97
                    // =========================================================================
                    if (gostStamp.IncludeCoverSheet)
                    {
                        FamilySymbol? coverSymbol = CoverSheetBuilder.FindOrLoadCoverTitleBlockSymbol(doc) ?? titleBlockSymbol;
                        string coverNumber = FormatSheetNumber(currentSeq);
                        ViewSheet coverSheet = CoverSheetBuilder.CreateOrUpdateCoverSheet(
                            doc, coverSymbol, gostStamp, currentSeq, totalSheetsCount, coverNumber);

                        sheetCatalog.Add(new KzGostSheetStyler.ArSheetCatalogEntry(
                            coverSheet.SheetNumber,
                            "Обложка",
                            ""));
                        scaleReportLines.Add($"   • Лист {coverSheet.SheetNumber} «{coverSheet.Name}» — Обложка (Имя: {gostStamp.FormattedDeveloperName}, Группа: {gostStamp.StudentGroup})");
                        placedArViewsCount++;
                        currentSeq++;
                    }

                    // =========================================================================
                    // 2. Лист 2: Общие данные по рабочим чертежам (ГОСТ 21.101-97 / СН РК)
                    // =========================================================================
                    ViewSheet? generalDataSheet = null;
                    int generalDataSeq = 0;
                    if (gostStamp.IncludeGeneralDataSheet)
                    {
                        generalDataSeq = currentSeq;
                        string gdNumber = FormatSheetNumber(currentSeq);
                        generalDataSheet = KzGostSheetStyler.CreateOrUpdateGeneralDataSheetRk(
                            doc,
                            titleBlockSymbol,
                            gostStamp,
                            sheetCatalog,
                            buildingBBox,
                            sheetSequenceNumber: currentSeq,
                            totalSheetsCount: totalSheetsCount);
                        AssignUniqueSheetNumber(doc, generalDataSheet, gdNumber);

                        sheetCatalog.Add(new KzGostSheetStyler.ArSheetCatalogEntry(
                            generalDataSheet.SheetNumber,
                            "Общие данные по рабочим чертежам",
                            ""));

                        scaleReportLines.Add($"   • Лист {generalDataSheet.SheetNumber} «{generalDataSheet.Name}» — Ведомость АР, ТЭП и Общие указания СН РК");
                        placedArViewsCount++;
                        currentSeq++;
                    }

                    // =========================================================================
                    // 3. Лист 3: Ситуационная схема по ГОСТ 21.508-93
                    // =========================================================================
                    if (gostStamp.IncludeSituationPlanSheet)
                    {
                        string sitNumber = FormatSheetNumber(currentSeq);
                        ViewSheet sitSheet = SituationPlanSheetBuilder.CreateOrUpdateSituationPlanSheet(
                            doc, titleBlockSymbol, gostStamp, buildingBBox, currentSeq, totalSheetsCount, sitNumber);

                        sheetCatalog.Add(new KzGostSheetStyler.ArSheetCatalogEntry(
                            sitSheet.SheetNumber,
                            "Ситуационная схема",
                            "М 1:5000"));
                        scaleReportLines.Add($"   • Лист {sitSheet.SheetNumber} «{sitSheet.Name}» — Ситуационная схема по ГОСТ (М 1:5000, адрес: {gostStamp.ProjectAddress})");
                        placedArViewsCount++;
                        currentSeq++;
                    }

                    // =========================================================================
                    // 4. Лист 4: Схема генерального плана (ГП-1)
                    // =========================================================================
                    if (gostStamp.IncludeSitePlanSheet)
                    {
                        string gpNumber = FormatSheetNumber(currentSeq);
                        ViewSheet? gpSheet = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewSheet))
                            .Cast<ViewSheet>()
                            .FirstOrDefault(s => !s.IsTemplate &&
                                (s.SheetNumber.Equals(gpNumber, StringComparison.OrdinalIgnoreCase) ||
                                 s.SheetNumber.Equals("ГП-1", StringComparison.OrdinalIgnoreCase) ||
                                 s.Name.Equals("Схема генерального плана", StringComparison.OrdinalIgnoreCase)));

                        if (gpSheet != null)
                        {
                            AssignUniqueSheetNumber(doc, gpSheet, gpNumber);
                            ConfigureSheetTitleBlockInstanceA3(doc, gpSheet);
                            GostStampHelper.ApplyGostStampToSheet(doc, gpSheet, gostStamp, currentSeq, totalSheetsCount);

                            sheetCatalog.Add(new KzGostSheetStyler.ArSheetCatalogEntry(
                                gpSheet.SheetNumber,
                                "Схема генерального плана",
                                $"М 1:{gostStamp.SitePlanScale}"));
                            scaleReportLines.Add($"   • Лист {gpSheet.SheetNumber} «{gpSheet.Name}» — 3D-модель генплана, зоны и ТЭП (М 1:{gostStamp.SitePlanScale})");
                            placedArViewsCount++;
                        }
                        currentSeq++;
                    }

                    // =========================================================================
                    // 4, 5, 6. Виды модели: Планы этажей по порядку -> Разрезы -> Фасады -> Прочие
                    // =========================================================================
                    foreach (View view in orderedModelViews)
                    {
                        int? userForcedScale = (customScalesByLong.TryGetValue(view.Id.Value, out int scLong) && scLong > 0)
                            ? scLong
                            : (customViewScales.TryGetValue(view.Id, out int scId) && scId > 0 ? scId : null);

                        // Устанавливаем выбранный пользователем масштаб сразу
                        if (userForcedScale is > 0)
                        {
                            SheetLayoutEngine.TrySetViewScale(view, userForcedScale.Value);
                        }

                        // Подготавливаем вид: скрываем элементы 3D-генплана, включаем HLR/Fine и подрезаем CropBox точно по зданию
                        SheetLayoutEngine.PrepareArchitecturalViewForSheet(doc, view, buildingBBox);
                        doc.Regenerate();

                        ViewSheet targetSheet = viewToAssignedSheet[view.Id];
                        bool isExistingSheet = existingSheetIds.Contains(targetSheet.Id);

                        // Очищаем любые старые деталь-линии и текстовые заметки на листе (включая старые оси)
                        DeleteSheetDetailElements(doc, targetSheet);

                        // Гарантируем отсутствие видовых экранов любых других видов на данном листе (строго 1 вид = 1 лист)
                        List<ElementId> remainingForeignVpIds = [];
                        foreach (ElementId vId in targetSheet.GetAllViewports())
                        {
                            if (doc.GetElement(vId) is Viewport otherVp && otherVp.ViewId != view.Id)
                            {
                                remainingForeignVpIds.Add(vId);
                            }
                        }
                        if (remainingForeignVpIds.Count > 0)
                        {
                            try { doc.Delete(remainingForeignVpIds); doc.Regenerate(); } catch { }
                        }

                        // Устанавливаем рамку Форма 3 (А3_А)
                        Element? tbInst = new FilteredElementCollector(doc, targetSheet.Id)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsNotElementType()
                            .FirstOrDefault();
                        if (tbInst == null)
                        {
                            doc.Create.NewFamilyInstance(XYZ.Zero, titleBlockSymbol, targetSheet);
                        }
                        else if (tbInst.GetTypeId() != titleBlockSymbol.Id)
                        {
                            tbInst.ChangeTypeId(titleBlockSymbol.Id);
                        }

                        string gostTitleWithoutScale = SheetLayoutEngine.FormatGostViewTitle(doc, view, scale: 0);
                        if (!string.IsNullOrWhiteSpace(gostTitleWithoutScale) && NamingUtils.IsValidName(gostTitleWithoutScale))
                        {
                            try { targetSheet.Name = gostTitleWithoutScale; } catch { }
                        }

                        string currentSheetNum = FormatSheetNumber(currentSeq);
                        AssignUniqueSheetNumber(doc, targetSheet, currentSheetNum);
                        ConfigureSheetTitleBlockInstanceA3(doc, targetSheet);
                        GostStampHelper.ApplyGostStampToSheet(
                            doc,
                            targetSheet,
                            gostStamp,
                            currentSeq,
                            totalSheetsCount);
                        doc.Regenerate();

                        (double originX, double originY) = GetSheetOrigin(doc, targetSheet);

                        double schedWidthMm = 0.0;
                        bool isPlanView = view.ViewType is ViewType.FloorPlan or ViewType.AreaPlan or ViewType.EngineeringPlan;
                        bool isSectionView = view.ViewType == ViewType.Section;
                        bool isElevationView = view.ViewType == ViewType.Elevation;

                        List<ScheduleSheetInstance> existingScheds = new FilteredElementCollector(doc, targetSheet.Id)
                            .OfClass(typeof(ScheduleSheetInstance))
                            .Cast<ScheduleSheetInstance>()
                            .Where(si => !si.IsTitleblockRevisionSchedule)
                            .ToList();

                        bool isRoofPlan = view.Name.Contains("кровл", StringComparison.OrdinalIgnoreCase) ||
                                          view.Name.Contains("крыш", StringComparison.OrdinalIgnoreCase);

                        if (isPlanView && !isRoofPlan)
                        {
                            Level? viewLevel = (view as ViewPlan)?.GenLevel ??
                                (view.LevelId != ElementId.InvalidElementId ? doc.GetElement(view.LevelId) as Level : null);

                            ViewSchedule? levelRoomSchedule = GetOrCreateRoomScheduleForLevel(doc, viewLevel, targetSheet);

                            ScheduleSheetInstance? matchingSchedInstance = null;
                            foreach (ScheduleSheetInstance si in existingScheds)
                            {
                                if (levelRoomSchedule != null && si.ScheduleId == levelRoomSchedule.Id)
                                {
                                    matchingSchedInstance = si;
                                }
                                else
                                {
                                    // Удаляем спецификацию другого уровня или старую общую спецификацию с этого листа
                                    try { doc.Delete(si.Id); } catch { }
                                }
                            }

                            if (matchingSchedInstance != null)
                            {
                                schedWidthMm = Math.Max(185.0, RepositionExistingScheduleAboveStamp(doc, targetSheet, matchingSchedInstance, originX, originY));
                            }
                            else if (levelRoomSchedule != null)
                            {
                                double placedW = PlaceScheduleAboveStamp(doc, targetSheet, levelRoomSchedule, originX, originY);
                                if (placedW > 0)
                                {
                                    placedRoomSchedulesCount++;
                                }
                                schedWidthMm = Math.Max(185.0, placedW);
                            }
                            else
                            {
                                schedWidthMm = 185.0;
                            }
                        }
                        else if (isRoofPlan)
                        {
                            schedWidthMm = 185.0;
                        }
                        else if (isSectionView || isElevationView)
                        {
                            // Резервируем правую колонку 140 мм над штампом:
                            // - на разрезах: под Ведомость составов конструкций (СП РК) и Экспликацию полов (ГОСТ 21.501 Форма 4)
                            // - на фасадах: под Ведомость наружной отделки фасадов (СТ РК / RAL) и Примечания
                            schedWidthMm = 140.0;
                        }

                        // Если текущий вид — не план этажа с помещениями (например, разрез, фасад или план кровли),
                        // гарантированно удаляем с листа спецификации помещений, оставшиеся от старых планов!
                        if (!isPlanView || isRoofPlan)
                        {
                            List<ScheduleSheetInstance> strayScheds = new FilteredElementCollector(doc, targetSheet.Id)
                                .OfClass(typeof(ScheduleSheetInstance))
                                .Cast<ScheduleSheetInstance>()
                                .Where(si => !si.IsTitleblockRevisionSchedule)
                                .ToList();
                            if (strayScheds.Count > 0)
                            {
                                try
                                {
                                    doc.Delete(strayScheds.Select(si => si.Id).ToList());
                                    doc.Regenerate();
                                }
                                catch { }
                            }
                        }

                        (double zoneMinXFt, double zoneMinYFt, double zoneMaxXFt, double zoneMaxYFt) =
                            SheetLayoutEngine.ComputeOptimalViewportZoneFt(
                                originX,
                                originY,
                                schedWidthMm,
                                hasScheduleOnRight: schedWidthMm > 0);

                        Viewport? vp = new FilteredElementCollector(doc, targetSheet.Id)
                            .OfClass(typeof(Viewport))
                            .Cast<Viewport>()
                            .FirstOrDefault(v => v.ViewId == view.Id);

                        if (vp == null)
                        {
                            if (!Viewport.CanAddViewToSheet(doc, targetSheet.Id, view.Id))
                            {
                                RemoveViewFromAnySheet(doc, view.Id);
                                doc.Regenerate();
                            }

                            XYZ initialCenter = new XYZ(
                                (zoneMinXFt + zoneMaxXFt) / 2.0,
                                (zoneMinYFt + zoneMaxYFt) / 2.0,
                                0.0);
                            vp = Viewport.Create(doc, targetSheet.Id, view.Id, initialCenter);
                            doc.Regenerate();
                        }

                        userForcedScale = (customScalesByLong.TryGetValue(view.Id.Value, out int scL) && scL > 0)
                            ? scL
                            : (customViewScales.TryGetValue(view.Id, out int sc) && sc > 0 ? sc : null);

                        int scaleBeforeFit = view.Scale;
                        int chosenScale = SheetLayoutEngine.FitAndCenterViewportInZone(
                            doc,
                            view,
                            vp,
                            zoneMinXFt,
                            zoneMinYFt,
                            zoneMaxXFt,
                            zoneMaxYFt,
                            userForcedScale: userForcedScale);

                        if (chosenScale != scaleBeforeFit)
                        {
                            ViewGostStyler.PrepareArchitecturalViewForGost(doc, view, buildingBBox);
                            doc.Regenerate();
                            SheetLayoutEngine.FitAndCenterViewportInZone(
                                doc,
                                view,
                                vp,
                                zoneMinXFt,
                                zoneMinYFt,
                                zoneMaxXFt,
                                zoneMaxYFt,
                                userForcedScale: chosenScale);
                        }

                        // Создаем оформленный по ГОСТ 21.501-93 заголовок над видом
                        SheetLayoutEngine.CreateOrUpdateGostViewTitleNote(doc, targetSheet, vp, view, chosenScale);

                        // Наносим специализированное ГОСТ РК / СН РК оформление в зависимости от типа листа
                        if (isPlanView)
                        {
                            KzGostSheetStyler.DecorateFloorPlanSheetRk(
                                doc, targetSheet, vp, view, originX, originY, buildingBBox, chosenScale);
                        }
                        else if (isSectionView && view is ViewSection secView)
                        {
                            double rightColLeftX = originX + ((420.0 - 5.0 - 140.0) * MmToFeet);
                            Outline secBox = vp.GetBoxOutline();
                            if (secBox.MaximumPoint.X > rightColLeftX - (3.0 * MmToFeet))
                            {
                                double shiftX = secBox.MaximumPoint.X - (rightColLeftX - (3.0 * MmToFeet));
                                vp.SetBoxCenter(vp.GetBoxCenter() - new XYZ(shiftX, 0.0, 0.0));
                                doc.Regenerate();
                            }

                            List<SectionSheetBuilder.ConstructionCallout> callouts = SectionSheetBuilder.ExtractModelConstructionCallouts(doc);
                            double startTopY = originY + ((297.0 - 5.0 - 5.0) * MmToFeet);
                            SectionSheetBuilder.DrawConstructionCalloutsOnSheet(doc, targetSheet, rightColLeftX, startTopY, 140.0, callouts);
                        }
                        else if (isElevationView)
                        {
                            KzGostSheetStyler.DecorateElevationSheetRk(
                                doc, targetSheet, vp, view, originX, originY, buildingBBox, chosenScale);
                        }

                        sheetCatalog.Add(new KzGostSheetStyler.ArSheetCatalogEntry(
                            targetSheet.SheetNumber,
                            gostTitleWithoutScale,
                            $"М 1:{chosenScale}"));

                        processedArSheets.Add((targetSheet, currentSeq, gostTitleWithoutScale));

                        if (scaleReportLines.Count < 15)
                        {
                            scaleReportLines.Add($"   • Лист {targetSheet.SheetNumber} «{targetSheet.Name}» — М 1:{chosenScale}");
                        }

                        if (isExistingSheet)
                            upgradedSheetsCount++;
                        else
                            placedArViewsCount++;

                        currentSeq++;
                    }

                    // =========================================================================
                    // 7. Лист 7: Визуализация («РЕНДЕР»)
                    // =========================================================================
                    if (gostStamp.IncludeRenderSheet)
                    {
                        string renderNumber = FormatSheetNumber(currentSeq);
                        ViewSheet renderSheet = RenderSheetBuilder.CreateOrUpdateRenderSheet(
                            doc, titleBlockSymbol, gostStamp, currentSeq, totalSheetsCount, renderNumber);

                        sheetCatalog.Add(new KzGostSheetStyler.ArSheetCatalogEntry(
                            renderSheet.SheetNumber,
                            "Визуализация",
                            ""));
                        scaleReportLines.Add($"   • Лист {renderSheet.SheetNumber} «{renderSheet.Name}» — Визуализация («РЕНДЕР»)");
                        placedArViewsCount++;
                        currentSeq++;
                    }

                    // Синхронизируем итоговое количество листов ("Листов") в основной надписи всех созданных листов АР
                    int finalTotalCount = totalSheetsCount;
                    foreach ((ViewSheet s, int seq, string _) in processedArSheets)
                    {
                        GostStampHelper.ApplyGostStampToSheet(doc, s, gostStamp, seq, finalTotalCount);
                    }

                    if (generalDataSheet != null)
                    {
                        KzGostSheetStyler.CreateOrUpdateGeneralDataSheetRk(
                            doc,
                            titleBlockSymbol,
                            gostStamp,
                            sheetCatalog,
                            buildingBBox,
                            sheetSequenceNumber: generalDataSeq,
                            totalSheetsCount: finalTotalCount,
                            targetSheet: generalDataSheet);
                    }

                    // Удаляем временные неиспользуемые пустые листы TMP-AR-*, если такие остались
                    List<ElementId> emptyTmpSheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsTemplate && s.SheetNumber.StartsWith("TMP-AR-", StringComparison.OrdinalIgnoreCase) && s.GetAllViewports().Count == 0)
                        .Select(s => s.Id)
                        .ToList();
                    if (emptyTmpSheets.Count > 0)
                    {
                        try { doc.Delete(emptyTmpSheets); } catch { }
                    }

                    txAr.Commit();
                }

                string scaleSummaryBlock = scaleReportLines.Count > 0
                    ? "\nОформленные листы комплекта по ГОСТ РК / СН РК:\n" + string.Join("\n", scaleReportLines) + "\n"
                    : string.Empty;

                string siteReportBlock = siteSummary != null
                    ? $"2. 3D Генеральный план по ГОСТ 21.508-93 / СН РК 3.01-01-2013 (лист \"{siteSummary.SheetNumber} — {siteSummary.SheetName}\", М 1:{siteSummary.ViewScale}):\n" +
                      $"   • Стиль генплана: {siteSummary.StyleDescription}\n" +
                      $"   • Габариты модели здания: {siteSummary.BuildingWidthM:F2} × {siteSummary.BuildingLengthM:F2} м\n" +
                      $"   • Площадь застройки: {siteSummary.BuildingAreaM2:F2} м²\n" +
                      $"   • 3D-отмостка, тротуары и бордюры (+1500 мм): {siteSummary.BlindAreaM2:F2} м²\n" +
                      $"   • 3D-дороги и парковки ({siteSummary.ParkingSpotsCount} м/м 2.5×5.0 м): {siteSummary.RoadsAndParkingAreaM2:F2} м²\n" +
                      $"   • 3D-газон и озеленение (+15000 мм, {siteSummary.CreatedTreesCount} 3D-деревьев): {siteSummary.GreenAreaM2:F2} м²\n" +
                      $"   • Общая площадь участка (баланс): {siteSummary.TotalSiteAreaM2:F2} м²"
                    : "2. Лист ГП-1 (Генеральный план): пропущен по выбору пользователя.";

                TaskDialog.Show(
                    "Авторазмещение видов на листы по ГОСТ РК и СН РК",
                    $"Выполнение успешно завершено!\n" +
                    $"Штамп ГОСТ 21.101-97 / СН РК: Стадия «{gostStamp.StageCode}» | Разраб.: {gostStamp.FormattedDeveloperName} | Дата: {gostStamp.IssueDateGost}\n" +
                    $"Формат листов: {titleBlockSymbol.FamilyName} — {titleBlockSymbol.Name} (А3, 420×297 мм)\n\n" +
                    $"1. Комплект архитектурных листов (марка АР):\n" +
                    $"   • Создано новых листов А3: {placedArViewsCount}\n" +
                    $"   • Обновлено существующих листов: {upgradedSheetsCount}\n" +
                    $"   • Удалено лишних (снятых галочкой) листов: {removedExtraSheetsCount}\n" +
                    $"   • Размещено экспликаций помещений: {placedRoomSchedulesCount}\n" +
                    (renumberedRoomsCount > 0 ? $"   • Нумерация помещений: упорядочено по ГОСТ {renumberedRoomsCount} помещений (1, 2, 3...)\n" : "") +
                    (gostStamp.ReplaceRoomTagsWithAdsk ? $"   • Марки помещений: заменено на ADSK: {replacedRoomTagsCount} шт., новых: {createdRoomTagsCount} шт.\n" : "") +
                    scaleSummaryBlock + "\n" +
                    siteReportBlock);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Ошибка выполнения плагина", $"Произошла ошибка:\n{ex.Message}");
                return Result.Failed;
            }
        }

        // =========================================================================
        // ЗАГРУЗКА ВСТРОЕННОГО СЕМЕЙСТВА "Лист (Форма3).rfa" И ОБНОВЛЕНИЕ ЛИСТОВ
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

        /// <summary>
        /// Ищет в проекте семейство "Лист (Форма3)" (тип "А3_А"), а если его нет — автоматически
        /// извлекает и загружает встроенный в плагин файл "Лист (Форма3).rfa", игнорируя чужие листы проекта.
        /// </summary>
        private static FamilySymbol? FindOrLoadCustomTitleBlockSymbol(Document doc)
        {
            FamilySymbol? customSymbol = FindSymbolInFamily(doc, CustomTitleBlockFamilyName, CustomTitleBlockTypeName);
            if (customSymbol != null)
            {
                return customSymbol;
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
                    // Если семейство уже было частично загружено, ищем его типоразмеры ниже
                }

                customSymbol = FindSymbolInFamily(doc, CustomTitleBlockFamilyName, CustomTitleBlockTypeName);
                if (customSymbol != null)
                {
                    return customSymbol;
                }
            }

            // Резервный поиск на случай другого шаблона
            List<FamilySymbol> allTitleBlocks = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .ToList();

            return allTitleBlocks.FirstOrDefault(tb =>
                       tb.Name.Equals(CustomTitleBlockTypeName, StringComparison.OrdinalIgnoreCase) ||
                       tb.Name.Contains("А3", StringComparison.OrdinalIgnoreCase) ||
                       tb.Name.Contains("A3", StringComparison.OrdinalIgnoreCase))
                   ?? allTitleBlocks.FirstOrDefault();
        }

        private static FamilySymbol? FindSymbolInFamily(Document doc, string familyName, string preferredTypeName)
        {
            List<FamilySymbol> symbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .Where(tb => tb.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase) ||
                             tb.FamilyName.Contains("Форма3", StringComparison.OrdinalIgnoreCase) ||
                             tb.FamilyName.Contains("Форма 3", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (symbols.Count == 0)
            {
                return null;
            }

            return symbols.FirstOrDefault(s => s.Name.Equals(preferredTypeName, StringComparison.OrdinalIgnoreCase))
                   ?? symbols.FirstOrDefault(s => s.Name.Contains("А3", StringComparison.OrdinalIgnoreCase) ||
                                                  s.Name.Contains("A3", StringComparison.OrdinalIgnoreCase))
                   ?? symbols[0];
        }

        /// <summary>
        /// Возвращает путь к файлу "Лист (Форма3).rfa" рядом с DLL или извлекает его из ресурсов сборки во временную папку.
        /// </summary>
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

        /// <summary>
        /// Настраивает параметры экземпляра основной надписи "Лист (Форма3)" на формат А3 горизонтальный (420х297 мм).
        /// </summary>
        private static void ConfigureSheetTitleBlockInstanceA3(Document doc, ViewSheet sheet)
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
                    // Игнорируем, если параметр управляется формулой типа
                }
            }
        }

        /// <summary>
        /// Обновляет все существующие архитектурные листы проекта: переводит рамку на "Лист (Форма3) : А3_А",
        /// заполняет штамп по ГОСТ Р 21.1101, скрывает элементы ГП на видах здания, подрезает CropBox,
        /// подбирает точный масштаб ГОСТ и центрирует видовые экраны по фактическому GetBoxOutline().
        /// </summary>
        private static int UpgradeExistingSheetsToCustomTitleBlock(
            Document doc,
            FamilySymbol customTitleBlockSymbol,
            GostStampData gostStamp,
            BoundingBoxXYZ? buildingBBox,
            List<string> scaleReportLines)
        {
            int upgradedCount = 0;

            List<ViewSheet> existingSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate && !s.SheetNumber.Equals("ГП-1", StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.SheetNumber)
                .ToList();

            int pageIdx = 1;

            foreach (ViewSheet sheet in existingSheets)
            {
                Element? tbInstance = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .FirstOrDefault();

                if (tbInstance == null)
                {
                    doc.Create.NewFamilyInstance(XYZ.Zero, customTitleBlockSymbol, sheet);
                }
                else if (tbInstance.GetTypeId() != customTitleBlockSymbol.Id)
                {
                    tbInstance.ChangeTypeId(customTitleBlockSymbol.Id);
                }

                ConfigureSheetTitleBlockInstanceA3(doc, sheet);
                GostStampHelper.ApplyGostStampToSheet(doc, sheet, gostStamp, pageIdx);
                doc.Regenerate();

                (double originX, double originY) = GetSheetOrigin(doc, sheet);

                // Проверяем наличие экспликации на листе или добавляем её для планов этажей
                List<ScheduleSheetInstance> sheetSchedules = new FilteredElementCollector(doc, sheet.Id)
                    .OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>()
                    .Where(si => !si.IsTitleblockRevisionSchedule)
                    .ToList();

                List<Viewport> viewports = new FilteredElementCollector(doc, sheet.Id)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .ToList();

                Level? sheetPlanLevel = null;
                foreach (Viewport vp in viewports)
                {
                    if (doc.GetElement(vp.ViewId) is View v &&
                        v.ViewType is ViewType.FloorPlan or ViewType.AreaPlan or ViewType.EngineeringPlan)
                    {
                        sheetPlanLevel = (v as ViewPlan)?.GenLevel ??
                            (v.LevelId != ElementId.InvalidElementId ? doc.GetElement(v.LevelId) as Level : null);
                        if (sheetPlanLevel != null) break;
                    }
                }

                ViewSchedule? sheetRoomSchedule = sheetPlanLevel != null
                    ? GetOrCreateRoomScheduleForLevel(doc, sheetPlanLevel, sheet)
                    : null;

                double schedWidthMm = 0.0;
                ScheduleSheetInstance? matchingInstance = null;
                foreach (ScheduleSheetInstance si in sheetSchedules)
                {
                    if (sheetRoomSchedule != null && si.ScheduleId == sheetRoomSchedule.Id)
                    {
                        matchingInstance = si;
                    }
                    else
                    {
                        try { doc.Delete(si.Id); } catch { }
                    }
                }

                if (matchingInstance != null)
                {
                    schedWidthMm = RepositionExistingScheduleAboveStamp(doc, sheet, matchingInstance, originX, originY);
                }
                else if (sheetRoomSchedule != null)
                {
                    schedWidthMm = PlaceScheduleAboveStamp(doc, sheet, sheetRoomSchedule, originX, originY);
                }
                else if (viewports.Any(vp => doc.GetElement(vp.ViewId) is View v && v.ViewType == ViewType.Section))
                {
                    schedWidthMm = 140.0;
                }

                (double zoneMinXFt, double zoneMinYFt, double zoneMaxXFt, double zoneMaxYFt) =
                    SheetLayoutEngine.ComputeOptimalViewportZoneFt(
                        originX,
                        originY,
                        schedWidthMm,
                        hasScheduleOnRight: schedWidthMm > 0 || sheetSchedules.Count > 0);

                if (viewports.Count == 1)
                {
                    Viewport vp = viewports[0];
                    if (doc.GetElement(vp.ViewId) is View view)
                    {
                        SheetLayoutEngine.PrepareArchitecturalViewForSheet(doc, view, buildingBBox);
                        doc.Regenerate();

                        int chosenScale = SheetLayoutEngine.FitAndCenterViewportInZone(
                            doc,
                            view,
                            vp,
                            zoneMinXFt,
                            zoneMinYFt,
                            zoneMaxXFt,
                            zoneMaxYFt);

                        // Создаем оформленный по ГОСТ заголовок над видом
                        SheetLayoutEngine.CreateOrUpdateGostViewTitleNote(doc, sheet, vp, view, chosenScale);

                        if (view is ViewSection secView && secView.ViewType == ViewType.Section)
                        {
                            double rightColLeftX = originX + ((420.0 - 5.0 - 140.0) * MmToFeet);
                            Outline secBox = vp.GetBoxOutline();
                            if (secBox.MaximumPoint.X > rightColLeftX - (3.0 * MmToFeet))
                            {
                                double shiftX = secBox.MaximumPoint.X - (rightColLeftX - (3.0 * MmToFeet));
                                vp.SetBoxCenter(vp.GetBoxCenter() - new XYZ(shiftX, 0.0, 0.0));
                                doc.Regenerate();
                            }

                            List<SectionSheetBuilder.ConstructionCallout> callouts = SectionSheetBuilder.ExtractModelConstructionCallouts(doc);
                            double startTopY = originY + ((297.0 - 5.0 - 5.0) * MmToFeet);
                            SectionSheetBuilder.DrawConstructionCalloutsOnSheet(doc, sheet, rightColLeftX, startTopY, 140.0, callouts);
                        }

                        if (scaleReportLines.Count < 8)
                        {
                            scaleReportLines.Add($"   • Лист {sheet.SheetNumber} «{sheet.Name}» — М 1:{chosenScale}");
                        }
                    }
                }
                else if (viewports.Count > 1)
                {
                    // Если на листе размещено несколько видов, делим рабочую зону равномерно по горизонтали
                    double totalWidthFt = zoneMaxXFt - zoneMinXFt;
                    double slotWidthFt = totalWidthFt / viewports.Count;

                    for (int i = 0; i < viewports.Count; i++)
                    {
                        Viewport vp = viewports[i];
                        if (doc.GetElement(vp.ViewId) is View view)
                        {
                            SheetLayoutEngine.PrepareArchitecturalViewForSheet(doc, view, buildingBBox);
                            doc.Regenerate();

                            double slotMinX = zoneMinXFt + (i * slotWidthFt);
                            double slotMaxX = slotMinX + slotWidthFt;

                            int chosenScale = SheetLayoutEngine.FitAndCenterViewportInZone(
                                doc,
                                view,
                                vp,
                                slotMinX,
                                zoneMinYFt,
                                slotMaxX,
                                zoneMaxYFt);

                            SheetLayoutEngine.CreateOrUpdateGostViewTitleNote(doc, sheet, vp, view, chosenScale);

                            if (i == 0 && scaleReportLines.Count < 8)
                            {
                                scaleReportLines.Add($"   • Лист {sheet.SheetNumber} «{sheet.Name}» — М 1:{chosenScale}");
                            }
                        }
                    }
                }

                upgradedCount++;
                pageIdx++;
            }

            return upgradedCount;
        }

        private static int RemoveUnselectedExistingArSheets(
            Document doc,
            HashSet<ElementId> selectedViewIds,
            GostStampData gostStamp)
        {
            int removedCount = 0;

            List<ViewSheet> existingSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate && !s.SheetNumber.Equals("ГП-1", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (ViewSheet sheet in existingSheets)
            {
                ICollection<ElementId> vpIds = sheet.GetAllViewports();

                // 1. Обложка
                if (sheet.Name.Equals("Обложка", StringComparison.OrdinalIgnoreCase) || sheet.Name.Contains("Титульн", StringComparison.OrdinalIgnoreCase))
                {
                    if (!gostStamp.IncludeCoverSheet)
                    {
                        try { doc.Delete(sheet.Id); removedCount++; } catch { }
                    }
                    continue;
                }

                // 2. Ситуационная схема
                if (sheet.Name.Equals("Ситуационная схема", StringComparison.OrdinalIgnoreCase) || sheet.Name.Contains("Ситуацион", StringComparison.OrdinalIgnoreCase))
                {
                    if (!gostStamp.IncludeSituationPlanSheet)
                    {
                        try { doc.Delete(sheet.Id); removedCount++; } catch { }
                    }
                    continue;
                }

                // 3. Генплан
                if (sheet.Name.Contains("генерального плана", StringComparison.OrdinalIgnoreCase))
                {
                    if (!gostStamp.IncludeSitePlanSheet)
                    {
                        try { doc.Delete(sheet.Id); removedCount++; } catch { }
                    }
                    continue;
                }

                // 4. Визуализация / Рендер
                if (sheet.Name.Equals("Визуализация", StringComparison.OrdinalIgnoreCase) || sheet.Name.Equals("Рендер", StringComparison.OrdinalIgnoreCase))
                {
                    if (!gostStamp.IncludeRenderSheet)
                    {
                        try { doc.Delete(sheet.Id); removedCount++; } catch { }
                    }
                    continue;
                }

                // 5. Если это безвидовой лист «Общие данные»
                if (vpIds.Count == 0 && sheet.Name.Contains("Общие данные", StringComparison.OrdinalIgnoreCase))
                {
                    if (!gostStamp.IncludeGeneralDataSheet)
                    {
                        try
                        {
                            doc.Delete(sheet.Id);
                            removedCount++;
                        }
                        catch
                        {
                        }
                    }
                    continue;
                }

                // 6. Обычные листы с видами
                bool hasAnySelectedView = false;
                foreach (ElementId vpId in vpIds)
                {
                    if (doc.GetElement(vpId) is Viewport vp && selectedViewIds.Contains(vp.ViewId))
                    {
                        hasAnySelectedView = true;
                        break;
                    }
                }

                if (!hasAnySelectedView && vpIds.Count > 0)
                {
                    try
                    {
                        doc.Delete(sheet.Id);
                        removedCount++;
                    }
                    catch
                    {
                        // Если лист открыт как активный вид и Revit не дает его удалить, удаляем с него видовые экраны
                        foreach (ElementId vpId in vpIds.ToList())
                        {
                            try { doc.Delete(vpId); } catch { }
                        }
                    }
                }
            }

            doc.Regenerate();
            return removedCount;
        }

        private static void DeleteSheetDetailElements(Document doc, ViewSheet sheet)
        {
            try
            {
                List<ElementId> idsToDelete = new FilteredElementCollector(doc, sheet.Id)
                    .WhereElementIsNotElementType()
                    .Where(e => e is TextNote or CurveElement)
                    .Select(e => e.Id)
                    .ToList();

                if (idsToDelete.Count > 0)
                {
                    doc.Delete(idsToDelete);
                }
            }
            catch
            {
            }
        }

        private static double RepositionExistingScheduleAboveStamp(
            Document doc,
            ViewSheet sheet,
            ScheduleSheetInstance instance,
            double originX,
            double originY)
        {
            BoundingBoxXYZ? box = instance.get_BoundingBox(sheet);
            if (box == null)
            {
                return 0.0;
            }

            double widthFt = Math.Abs(box.Max.X - box.Min.X);
            double widthMm = UnitUtils.ConvertFromInternalUnits(widthFt, UnitTypeId.Millimeters);

            double targetRightX = originX + ((SheetWidthMm - MarginRightMm - 2.0) * MmToFeet);
            double targetBottomY = originY + ((MarginBottomFrameMm + StampHeightMm + 2.0) * MmToFeet);

            XYZ moveVector = new XYZ(targetRightX - box.Max.X, targetBottomY - box.Min.Y, 0.0);
            ElementTransformUtils.MoveElement(doc, instance.Id, moveVector);

            return widthMm;
        }

        // =========================================================================
        // МЕТОДЫ ЭТАПА 1: СБОР ВИДОВ "АР_", ПОДБОР МАСШТАБА И РАЗМЕЩЕНИЕ НА ЛИСТАХ
        // =========================================================================

        private static List<View> GetUnplacedArViews(Document doc)
        {
            HashSet<ElementId> placedViewIds = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .Select(vp => vp.ViewId)
                .ToHashSet();

            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate
                            && !string.IsNullOrEmpty(v.Name)
                            && v.Name.StartsWith(RequiredViewPrefix, StringComparison.OrdinalIgnoreCase)
                            && v.CanBePrinted
                            && IsPlaceableViewType(v.ViewType)
                            && !placedViewIds.Contains(v.Id)
                            && string.IsNullOrEmpty(v.get_Parameter(BuiltInParameter.VIEWPORT_SHEET_NUMBER)?.AsString()))
                .ToList();
        }

        private static void EnsureArPrefixOnUnplacedViewsIfNoneExist(Document doc)
        {
            HashSet<ElementId> placedViewIds = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .Select(vp => vp.ViewId)
                .ToHashSet();

            bool anyUnplacedArViewExists = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Any(v => !v.IsTemplate
                          && !placedViewIds.Contains(v.Id)
                          && !string.IsNullOrEmpty(v.Name)
                          && v.Name.StartsWith(RequiredViewPrefix, StringComparison.OrdinalIgnoreCase));

            if (anyUnplacedArViewExists)
            {
                return;
            }

            List<View> candidateViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate
                            && v.CanBePrinted
                            && IsPlaceableViewType(v.ViewType)
                            && !placedViewIds.Contains(v.Id)
                            && !string.IsNullOrWhiteSpace(v.Name)
                            && !v.Name.StartsWith("ГП_", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (View view in candidateViews)
            {
                string newName = RequiredViewPrefix + view.Name;
                if (NamingUtils.IsValidName(newName))
                {
                    try
                    {
                        view.Name = newName;
                    }
                    catch
                    {
                        // Игнорируем, если имя уже занято или заблокировано
                    }
                }
            }
        }

        private static void ApplyOptimalStandardScale(View view, double usableWidthMm, double usableHeightMm)
        {
            if (view.CropBoxActive && view.CropBox != null)
            {
                BoundingBoxXYZ cropBox = view.CropBox;

                double viewWidthMm = UnitUtils.ConvertFromInternalUnits(
                    Math.Abs(cropBox.Max.X - cropBox.Min.X),
                    UnitTypeId.Millimeters);

                double viewHeightMm = UnitUtils.ConvertFromInternalUnits(
                    Math.Abs(cropBox.Max.Y - cropBox.Min.Y),
                    UnitTypeId.Millimeters);

                if (viewWidthMm > 1e-3 && viewHeightMm > 1e-3)
                {
                    int optimalScale = SelectStandardScale(viewWidthMm, viewHeightMm, usableWidthMm, usableHeightMm);
                    TrySetViewScale(view, optimalScale);
                }
            }
            else
            {
                BoundingBoxUV? outline = view.Outline;
                if (outline != null && view.Scale > 0)
                {
                    double paperWidthMm = UnitUtils.ConvertFromInternalUnits(
                        Math.Abs(outline.Max.U - outline.Min.U),
                        UnitTypeId.Millimeters);

                    double paperHeightMm = UnitUtils.ConvertFromInternalUnits(
                        Math.Abs(outline.Max.V - outline.Min.V),
                        UnitTypeId.Millimeters);

                    double modelWidthMm = paperWidthMm * view.Scale;
                    double modelHeightMm = paperHeightMm * view.Scale;

                    if (modelWidthMm > 1e-3 && modelHeightMm > 1e-3)
                    {
                        int optimalScale = SelectStandardScale(modelWidthMm, modelHeightMm, usableWidthMm, usableHeightMm);
                        TrySetViewScale(view, optimalScale);
                    }
                }
            }
        }

        // =========================================================================
        // МЕТОДЫ ЭТАПА 2: ГЕНЕРАЦИЯ ГЕНПЛАНА (ГП), OFFSET КОНТУРОВ, ТОПОТЕЛО И ТЭП
        // =========================================================================

        private sealed record SitePlanResult(
            string SheetNumber,
            string SheetName,
            double BuildingAreaM2,
            double BlindAreaM2,
            double RoadAreaM2,
            double HardscapeAreaM2,
            double GreenAreaM2,
            double TotalSiteAreaM2);

        private static SitePlanResult GenerateSitePlanAndSheet(Document doc, FamilySymbol titleBlockSymbol)
        {
            Level firstLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation))
                .ThenBy(l => l.Elevation)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("В проекте не найден ни один уровень (Level).");

            double baseZ = firstLevel.Elevation;

            CurveLoop buildingLoop = ExtractExteriorBuildingFootprint(doc, firstLevel, baseZ);

            if (!buildingLoop.IsCounterclockwise(XYZ.BasisZ))
            {
                buildingLoop.Flip();
            }

            CurveLoop blindAreaLoop = CurveLoop.CreateViaOffset(
                buildingLoop,
                BlindAreaOffsetMm * MmToFeet,
                XYZ.BasisZ);

            CurveLoop roadLoop = CurveLoop.CreateViaOffset(
                buildingLoop,
                RoadOffsetMm * MmToFeet,
                XYZ.BasisZ);

            CurveLoop siteBoundaryLoop = CurveLoop.CreateViaOffset(
                buildingLoop,
                SiteBoundaryOffsetMm * MmToFeet,
                XYZ.BasisZ);

            double buildingAreaM2 = CalculateCurveLoopAreaM2(buildingLoop);
            double blindOuterAreaM2 = CalculateCurveLoopAreaM2(blindAreaLoop);
            double roadOuterAreaM2 = CalculateCurveLoopAreaM2(roadLoop);
            double totalSiteAreaM2 = CalculateCurveLoopAreaM2(siteBoundaryLoop);

            double blindAreaM2 = Math.Max(0.0, blindOuterAreaM2 - buildingAreaM2);
            double roadAreaM2 = Math.Max(0.0, roadOuterAreaM2 - blindOuterAreaM2);
            double hardscapeAreaM2 = blindAreaM2 + roadAreaM2;
            double greenAreaM2 = Math.Max(0.0, totalSiteAreaM2 - roadOuterAreaM2);

            CreateSiteSurfacesAndSubdivisions(
                doc,
                firstLevel,
                buildingLoop,
                blindAreaLoop,
                roadLoop,
                siteBoundaryLoop);

            ViewPlan siteView = GetOrCreateSitePlanView(doc, firstLevel, siteBoundaryLoop);
            DrawSiteContourLines(doc, firstLevel.Elevation, buildingLoop, blindAreaLoop, roadLoop, siteBoundaryLoop);

            ViewSchedule siteSchedule = CreateSiteBalanceSchedule(
                doc,
                buildingAreaM2,
                blindAreaM2,
                roadAreaM2,
                hardscapeAreaM2,
                greenAreaM2,
                totalSiteAreaM2);

            // Если лист "ГП-1" уже был создан ранее, переиспользуем или создаем заново на "Лист (Форма3) : А3_А"
            ViewSheet? existingGpSheet = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .FirstOrDefault(s => !s.IsTemplate && s.SheetNumber.Equals("ГП-1", StringComparison.OrdinalIgnoreCase));

            if (existingGpSheet != null)
            {
                try
                {
                    doc.Delete(existingGpSheet.Id);
                    doc.Regenerate();
                }
                catch
                {
                    // Если лист активен, создадим новый с уникальным номером
                }
            }

            ViewSheet gpSheet = ViewSheet.Create(doc, titleBlockSymbol.Id);
            gpSheet.Name = "Схема генплана";
            AssignUniqueSheetNumber(doc, gpSheet, "ГП-1");

            ConfigureSheetTitleBlockInstanceA3(doc, gpSheet);
            doc.Regenerate();

            (double originX, double originY) = GetSheetOrigin(doc, gpSheet);

            double bottomOffsetMm = MarginBottomFrameMm + StampHeightMm; // 60 мм
            double usableWidthMm = SheetWidthMm - MarginLeftMm - MarginRightMm; // 395 мм
            double usableHeightMm = SheetHeightMm - MarginTopMm - bottomOffsetMm; // 232 мм

            double scheduleWidthMm = PlaceScheduleRightAboveStamp(doc, gpSheet, siteSchedule, originX, originY);

            double planUsableWidthMm = scheduleWidthMm > 0
                ? Math.Max(180.0, usableWidthMm - scheduleWidthMm - 10.0)
                : usableWidthMm;

            ApplyOptimalStandardScale(siteView, planUsableWidthMm, usableHeightMm);
            doc.Regenerate();

            double centerOffsetXMm = MarginLeftMm + (planUsableWidthMm / 2.0);
            double centerOffsetYMm = bottomOffsetMm + (usableHeightMm / 2.0);

            XYZ gpViewCenter = new XYZ(
                originX + (centerOffsetXMm * MmToFeet),
                originY + (centerOffsetYMm * MmToFeet),
                0.0);

            if (Viewport.CanAddViewToSheet(doc, gpSheet.Id, siteView.Id))
            {
                Viewport.Create(doc, gpSheet.Id, siteView.Id, gpViewCenter);
            }

            return new SitePlanResult(
                gpSheet.SheetNumber,
                gpSheet.Name,
                buildingAreaM2,
                blindAreaM2,
                roadAreaM2,
                hardscapeAreaM2,
                greenAreaM2,
                totalSiteAreaM2);
        }

        private static CurveLoop ExtractExteriorBuildingFootprint(Document doc, Level firstLevel, double planeZ)
        {
            List<Wall> allWalls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .OfType<Wall>()
                .ToList();

            List<Wall> exteriorWalls = allWalls
                .Where(w => w.WallType?.Function == WallFunction.Exterior
                            && (w.LevelId == firstLevel.Id || IsWallOnLevel(doc, w, firstLevel)))
                .ToList();

            if (exteriorWalls.Count == 0)
            {
                exteriorWalls = allWalls
                    .Where(w => w.WallType?.Function == WallFunction.Exterior)
                    .ToList();
            }

            if (exteriorWalls.Count == 0)
            {
                exteriorWalls = allWalls
                    .Where(w => w.LevelId == firstLevel.Id || IsWallOnLevel(doc, w, firstLevel))
                    .ToList();
            }

            if (exteriorWalls.Count == 0)
            {
                exteriorWalls = allWalls;
            }

            List<Curve> wallCurves = [];
            foreach (Wall wall in exteriorWalls)
            {
                if (wall.Location is LocationCurve { Curve: { } curve })
                {
                    XYZ p0 = curve.GetEndPoint(0);
                    XYZ p1 = curve.GetEndPoint(1);
                    XYZ flatStart = new XYZ(p0.X, p0.Y, planeZ);
                    XYZ flatEnd = new XYZ(p1.X, p1.Y, planeZ);

                    if (flatStart.DistanceTo(flatEnd) > 1e-3)
                    {
                        wallCurves.Add(Line.CreateBound(flatStart, flatEnd));
                    }
                }
            }

            if (TryChainCurvesIntoClosedLoop(wallCurves, out CurveLoop? chainedLoop) && chainedLoop != null)
            {
                return chainedLoop;
            }

            if (wallCurves.Count > 0)
            {
                List<XYZ> points = wallCurves
                    .SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1) })
                    .ToList();

                double minX = points.Min(p => p.X);
                double maxX = points.Max(p => p.X);
                double minY = points.Min(p => p.Y);
                double maxY = points.Max(p => p.Y);

                if ((maxX - minX) > 1.0 && (maxY - minY) > 1.0)
                {
                    return CreateRectangularLoop(minX, minY, maxX, maxY, planeZ);
                }
            }

            double fallbackHalfSize = 6000.0 * MmToFeet;
            return CreateRectangularLoop(-fallbackHalfSize, -fallbackHalfSize, fallbackHalfSize, fallbackHalfSize, planeZ);
        }

        private static bool IsWallOnLevel(Document doc, Wall wall, Level targetLevel)
        {
            if (doc.GetElement(wall.LevelId) is Level wallLevel)
            {
                return Math.Abs(wallLevel.Elevation - targetLevel.Elevation) < 1.5;
            }
            return false;
        }

        private static bool TryChainCurvesIntoClosedLoop(List<Curve> segments, out CurveLoop? resultLoop)
        {
            resultLoop = null;
            if (segments.Count < 3)
            {
                return false;
            }

            const double snapTolerance = 1.0;
            List<XYZ> remainingStarts = segments.Select(s => s.GetEndPoint(0)).ToList();
            List<XYZ> remainingEnds = segments.Select(s => s.GetEndPoint(1)).ToList();
            bool[] used = new bool[segments.Count];

            List<XYZ> orderedVertices = [remainingStarts[0], remainingEnds[0]];
            used[0] = true;

            for (int step = 1; step < segments.Count; step++)
            {
                XYZ currentEnd = orderedVertices[^1];
                int bestIndex = -1;
                bool reverse = false;
                double bestDist = snapTolerance;

                for (int i = 0; i < segments.Count; i++)
                {
                    if (used[i])
                    {
                        continue;
                    }

                    double d0 = currentEnd.DistanceTo(remainingStarts[i]);
                    if (d0 < bestDist)
                    {
                        bestDist = d0;
                        bestIndex = i;
                        reverse = false;
                    }

                    double d1 = currentEnd.DistanceTo(remainingEnds[i]);
                    if (d1 < bestDist)
                    {
                        bestDist = d1;
                        bestIndex = i;
                        reverse = true;
                    }
                }

                if (bestIndex < 0)
                {
                    break;
                }

                used[bestIndex] = true;
                XYZ nextVertex = reverse ? remainingStarts[bestIndex] : remainingEnds[bestIndex];

                if (nextVertex.DistanceTo(orderedVertices[0]) < snapTolerance && orderedVertices.Count >= 3)
                {
                    break;
                }

                if (nextVertex.DistanceTo(orderedVertices[^1]) > 1e-3)
                {
                    orderedVertices.Add(nextVertex);
                }
            }

            if (orderedVertices.Count < 3 || orderedVertices[^1].DistanceTo(orderedVertices[0]) > snapTolerance * 2.0)
            {
                return false;
            }

            try
            {
                CurveLoop loop = new CurveLoop();
                for (int i = 0; i < orderedVertices.Count; i++)
                {
                    XYZ p1 = orderedVertices[i];
                    XYZ p2 = orderedVertices[(i + 1) % orderedVertices.Count];
                    if (p1.DistanceTo(p2) > 1e-3)
                    {
                        loop.Append(Line.CreateBound(p1, p2));
                    }
                }

                if (!loop.IsOpen() && BoundaryValidation.IsValidHorizontalBoundary([loop]))
                {
                    CurveLoop testLoop = CurveLoop.CreateViaOffset(loop, 100.0 * MmToFeet, XYZ.BasisZ);
                    if (testLoop != null)
                    {
                        resultLoop = loop;
                        return true;
                    }
                }
            }
            catch
            {
                // Переходим к габаритному контуру при самопересечениях
            }

            return false;
        }

        private static CurveLoop CreateRectangularLoop(double minX, double minY, double maxX, double maxY, double z)
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
                double areaSqFeet = extrusion.Volume;
                return UnitUtils.ConvertFromInternalUnits(areaSqFeet, UnitTypeId.SquareMeters);
            }
            catch
            {
                List<XYZ> pts = [];
                foreach (Curve c in loop)
                {
                    IList<XYZ> tess = c.Tessellate();
                    for (int i = 0; i < tess.Count - 1; i++)
                    {
                        pts.Add(tess[i]);
                    }
                }

                double doubleAreaSqFt = 0.0;
                for (int i = 0; i < pts.Count; i++)
                {
                    XYZ a = pts[i];
                    XYZ b = pts[(i + 1) % pts.Count];
                    doubleAreaSqFt += (a.X * b.Y) - (b.X * a.Y);
                }

                double areaSqFeet = Math.Abs(doubleAreaSqFt) * 0.5;
                return UnitUtils.ConvertFromInternalUnits(areaSqFeet, UnitTypeId.SquareMeters);
            }
        }

        private static void CreateSiteSurfacesAndSubdivisions(
            Document doc,
            Level level,
            CurveLoop buildingLoop,
            CurveLoop blindAreaLoop,
            CurveLoop roadLoop,
            CurveLoop siteBoundaryLoop)
        {
            List<ElementId> previousElements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e is Toposolid or Floor)
                .Where(e =>
                {
                    string? comment = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                    return comment != null && comment.StartsWith("ГП:", StringComparison.OrdinalIgnoreCase);
                })
                .Select(e => e.Id)
                .ToList();

            if (previousElements.Count > 0)
            {
                try
                {
                    doc.Delete(previousElements);
                    doc.Regenerate();
                }
                catch
                {
                    // Продолжаем создание
                }
            }

            ElementId topoTypeId = new FilteredElementCollector(doc)
                .OfClass(typeof(ToposolidType))
                .FirstElementId();

            if (topoTypeId != ElementId.InvalidElementId)
            {
                try
                {
                    CurveLoop outerSiteLoop = CurveLoop.CreateViaCopy(siteBoundaryLoop);
                    Toposolid hostToposolid = Toposolid.Create(doc, [outerSiteLoop], topoTypeId, level.Id);
                    SetElementMetadata(hostToposolid, "ГП: Озеленение и благоустройство (+15000 мм)", "ГП-ОЗ");

                    doc.Regenerate();

                    CurveLoop blindOuter = CurveLoop.CreateViaCopy(blindAreaLoop);
                    CurveLoop blindInner = CurveLoop.CreateViaCopy(buildingLoop);
                    blindInner.Flip();
                    Toposolid blindSub = hostToposolid.CreateSubDivision(doc, [blindOuter, blindInner]);
                    SetElementMetadata(blindSub, "ГП: Отмостка / пешеходная зона (+1500 мм)", "ГП-ОТМ");

                    CurveLoop roadOuter = CurveLoop.CreateViaCopy(roadLoop);
                    CurveLoop roadInner = CurveLoop.CreateViaCopy(blindAreaLoop);
                    roadInner.Flip();
                    Toposolid roadSub = hostToposolid.CreateSubDivision(doc, [roadOuter, roadInner]);
                    SetElementMetadata(roadSub, "ГП: Проезд / автодорога (+6000 мм)", "ГП-ПР");
                }
                catch
                {
                    // Ниже создаются перекрытия Floor
                }
            }

            ElementId floorTypeId = Floor.GetDefaultFloorType(doc, false);
            if (floorTypeId == ElementId.InvalidElementId)
            {
                floorTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(FloorType))
                    .FirstElementId();
            }

            if (floorTypeId != ElementId.InvalidElementId)
            {
                TryCreateZoneFloor(doc, [CurveLoop.CreateViaCopy(buildingLoop)], floorTypeId, level.Id,
                    "ГП: Площадь застройки здания", "1", -20.0 * MmToFeet);

                CurveLoop bOuter = CurveLoop.CreateViaCopy(blindAreaLoop);
                CurveLoop bInner = CurveLoop.CreateViaCopy(buildingLoop);
                bInner.Flip();
                TryCreateZoneFloor(doc, [bOuter, bInner], floorTypeId, level.Id,
                    "ГП: Твердое покрытие — Отмостка (+1500 мм)", "2", -30.0 * MmToFeet);

                CurveLoop rOuter = CurveLoop.CreateViaCopy(roadLoop);
                CurveLoop rInner = CurveLoop.CreateViaCopy(blindAreaLoop);
                rInner.Flip();
                TryCreateZoneFloor(doc, [rOuter, rInner], floorTypeId, level.Id,
                    "ГП: Твердое покрытие — Проезд (+6000 мм)", "3", -40.0 * MmToFeet);

                CurveLoop sOuter = CurveLoop.CreateViaCopy(siteBoundaryLoop);
                CurveLoop sInner = CurveLoop.CreateViaCopy(roadLoop);
                sInner.Flip();
                TryCreateZoneFloor(doc, [sOuter, sInner], floorTypeId, level.Id,
                    "ГП: Озеленение территории (+15000 мм)", "4", -50.0 * MmToFeet);
            }
        }

        private static void TryCreateZoneFloor(
            Document doc,
            IList<CurveLoop> profile,
            ElementId floorTypeId,
            ElementId levelId,
            string comment,
            string mark,
            double heightOffsetFeet)
        {
            try
            {
                Floor floor = Floor.Create(doc, profile, floorTypeId, levelId);
                SetElementMetadata(floor, comment, mark);

                Parameter? offsetParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (offsetParam is { IsReadOnly: false })
                {
                    offsetParam.Set(heightOffsetFeet);
                }
            }
            catch
            {
                // Пропускаем в случае геометрических ограничений конкретного типа перекрытия
            }
        }

        private static void SetElementMetadata(Element element, string comment, string mark)
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

        private static void DrawSiteContourLines(
            Document doc,
            double elevation,
            params CurveLoop[] loops)
        {
            try
            {
                Plane plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0.0, 0.0, elevation));
                SketchPlane sketchPlane = SketchPlane.Create(doc, plane);

                foreach (CurveLoop loop in loops)
                {
                    foreach (Curve curve in loop)
                    {
                        doc.Create.NewModelCurve(curve, sketchPlane);
                    }
                }
            }
            catch
            {
                // Если линии уже созданы или плоскость недоступна — не прерываем работу
            }
        }

        private static ViewPlan GetOrCreateSitePlanView(Document doc, Level firstLevel, CurveLoop siteBoundaryLoop)
        {
            const string siteViewName = "ГП_Схема генерального плана";

            HashSet<ElementId> placedViewIds = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .Select(vp => vp.ViewId)
                .ToHashSet();

            ViewPlan? existingSiteView = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate
                                     && v.Name.Equals(siteViewName, StringComparison.OrdinalIgnoreCase)
                                     && !placedViewIds.Contains(v.Id));

            if (existingSiteView == null)
            {
                ElementId viewFamilyTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily is ViewFamily.FloorPlan or ViewFamily.AreaPlan)?.Id
                    ?? throw new InvalidOperationException("Не найден тип вида плана этажа (ViewFamilyType).");

                existingSiteView = ViewPlan.Create(doc, viewFamilyTypeId, firstLevel.Id);
                AssignUniqueViewName(doc, existingSiteView, siteViewName);
            }

            List<XYZ> boundaryPts = siteBoundaryLoop
                .SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1) })
                .ToList();

            double pad = 2000.0 * MmToFeet;
            double minX = boundaryPts.Min(p => p.X) - pad;
            double maxX = boundaryPts.Max(p => p.X) + pad;
            double minY = boundaryPts.Min(p => p.Y) - pad;
            double maxY = boundaryPts.Max(p => p.Y) + pad;

            existingSiteView.CropBoxActive = true;
            existingSiteView.CropBoxVisible = true;

            BoundingBoxXYZ cropBox = existingSiteView.CropBox;
            cropBox.Min = new XYZ(minX, minY, cropBox.Min.Z);
            cropBox.Max = new XYZ(maxX, maxY, cropBox.Max.Z);
            existingSiteView.CropBox = cropBox;

            return existingSiteView;
        }

        private static ViewSchedule CreateSiteBalanceSchedule(
            Document doc,
            double buildingAreaM2,
            double blindAreaM2,
            double roadAreaM2,
            double hardscapeAreaM2,
            double greenAreaM2,
            double totalSiteAreaM2)
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
                    // Если не удалось удалить, создадим с уникальным именем
                }
            }

            ElementId floorsCategoryId = new ElementId(BuiltInCategory.OST_Floors);
            ViewSchedule schedule = ViewSchedule.CreateSchedule(doc, floorsCategoryId);
            AssignUniqueViewName(doc, schedule, scheduleName);

            ScheduleDefinition definition = schedule.Definition;
            IList<SchedulableField> schedulableFields = definition.GetSchedulableFields();

            ScheduleField? markField = AddScheduleField(definition, schedulableFields, BuiltInParameter.ALL_MODEL_MARK, "№", 15.0);
            ScheduleField? commentField = AddScheduleField(definition, schedulableFields, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Наименование зоны ГП", 85.0);
            ScheduleField? areaField = AddScheduleField(definition, schedulableFields, BuiltInParameter.HOST_AREA_COMPUTED, "Площадь, м²", 35.0);

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
                "Баланс: Застр. {0:F1} м² | Покр. {1:F1} м² (Отм. {2:F1} + Пр. {3:F1}) | Озел. {4:F1} м² | Всего: {5:F1} м²",
                buildingAreaM2,
                hardscapeAreaM2,
                blindAreaM2,
                roadAreaM2,
                greenAreaM2,
                totalSiteAreaM2);

            doc.Regenerate();
            return schedule;
        }

        // =========================================================================
        // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ РАЗМЕЩЕНИЯ НА ЛИСТАХ И ЭКСПЛИКАЦИИ ПОМЕЩЕНИЙ
        // =========================================================================

        private static double PlaceScheduleRightAboveStamp(
            Document doc,
            ViewSheet sheet,
            ViewSchedule schedule,
            double originX,
            double originY)
        {
            try
            {
                ScheduleSheetInstance instance;
                try
                {
                    instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, XYZ.Zero);
                }
                catch
                {
                    // Если спецификация уже размещена на другом листе, создаем копию специально для этого листа
                    ElementId dupId = schedule.Duplicate(ViewDuplicateOption.Duplicate);
                    ViewSchedule dupSchedule = (ViewSchedule)doc.GetElement(dupId);
                    AssignUniqueViewName(doc, dupSchedule, $"{schedule.Name} ({sheet.SheetNumber})");
                    instance = ScheduleSheetInstance.Create(doc, sheet.Id, dupSchedule.Id, XYZ.Zero);
                }

                doc.Regenerate();

                BoundingBoxXYZ? box = instance.get_BoundingBox(sheet);
                if (box == null)
                {
                    return 135.0;
                }

                double widthFt = Math.Abs(box.Max.X - box.Min.X);
                double widthMm = UnitUtils.ConvertFromInternalUnits(widthFt, UnitTypeId.Millimeters);

                // Правый нижний угол таблицы ставим прямо над штампом (Y = 5 + 55 + 2 = 62 мм, X = 420 - 5 - 2 = 413 мм)
                double targetRightX = originX + ((SheetWidthMm - MarginRightMm - 2.0) * MmToFeet);
                double targetBottomY = originY + ((MarginBottomFrameMm + StampHeightMm + 2.0) * MmToFeet);

                XYZ moveVector = new XYZ(targetRightX - box.Max.X, targetBottomY - box.Min.Y, 0.0);
                ElementTransformUtils.MoveElement(doc, instance.Id, moveVector);

                return widthMm;
            }
            catch
            {
                return 0.0;
            }
        }

        private static double PlaceScheduleAboveStamp(
            Document doc,
            ViewSheet sheet,
            ViewSchedule schedule,
            double originX,
            double originY)
        {
            return PlaceScheduleRightAboveStamp(doc, sheet, schedule, originX, originY);
        }

        /// <summary>
        /// Санитизирует любые существующие спецификации помещений проекта:
        /// добавляет фильтр Площадь > 0, что полностью исключает неразмещенные (Not Placed) 
        /// и незамкнутые (Not Enclosed) помещения.
        /// </summary>
        private static void CleanExistingRoomSchedules(Document doc)
        {
            try
            {
                ElementId roomsCategoryId = new ElementId(BuiltInCategory.OST_Rooms);
                List<ViewSchedule> schedules = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => !s.IsTemplate
                                && !s.IsTitleblockRevisionSchedule
                                && !s.IsInternalKeynoteSchedule
                                && s.Definition.CategoryId == roomsCategoryId)
                    .ToList();

                foreach (ViewSchedule schedule in schedules)
                {
                    try
                    {
                        ScheduleDefinition def = schedule.Definition;
                        IList<SchedulableField> schedulableFields = def.GetSchedulableFields();
                        ScheduleField? areaField = EnsureScheduleField(def, schedulableFields, BuiltInParameter.ROOM_AREA, "Площадь, м²", 30.0);
                        if (areaField != null)
                        {
                            bool hasAreaFilter = false;
                            for (int i = 0; i < def.GetFilterCount(); i++)
                            {
                                ScheduleFilter f = def.GetFilter(i);
                                if (f.FieldId == areaField.FieldId && f.FilterType == ScheduleFilterType.GreaterThan)
                                {
                                    hasAreaFilter = true;
                                    break;
                                }
                            }

                            if (!hasAreaFilter && def.CanFilterByValue(areaField.FieldId))
                            {
                                def.AddFilter(new ScheduleFilter(areaField.FieldId, ScheduleFilterType.GreaterThan, 0.0));
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Находит или создает экспликацию помещений по ГОСТ 21.501-93 Форма 7 
        /// с фильтрацией строго по уровню этажа (Level) и исключением неразмещенных помещений (Area > 0).
        /// </summary>
        private static ViewSchedule? GetOrCreateRoomScheduleForLevel(
            Document doc,
            Level? level,
            ViewSheet? targetSheet = null)
        {
            ElementId roomsCategoryId = new ElementId(BuiltInCategory.OST_Rooms);

            string scheduleBaseName = level != null
                ? $"Экспликация помещений ({level.Name})"
                : "Экспликация помещений";

            List<ViewSchedule> allRoomSchedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate
                            && !s.IsTitleblockRevisionSchedule
                            && !s.IsInternalKeynoteSchedule
                            && s.Definition.CategoryId == roomsCategoryId)
                .ToList();

            ILookup<ElementId, ElementId> scheduleToSheetsMap = new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .Where(ssi => !ssi.IsTitleblockRevisionSchedule)
                .ToLookup(ssi => ssi.ScheduleId, ssi => ssi.OwnerViewId);

            ViewSchedule? schedule = null;

            // 1. Ищем существующую спецификацию для этого этажа, не занятую другим листом
            foreach (ViewSchedule s in allRoomSchedules)
            {
                if (s.Name.StartsWith(scheduleBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    List<ElementId> sheetIds = scheduleToSheetsMap[s.Id].ToList();
                    if (sheetIds.Count == 0 || (targetSheet != null && sheetIds.Contains(targetSheet.Id)))
                    {
                        schedule = s;
                        break;
                    }
                }
            }

            // 2. Если свободной нет, создаем новую спецификацию
            if (schedule == null)
            {
                try
                {
                    schedule = ViewSchedule.CreateSchedule(doc, roomsCategoryId);
                    string desiredName = scheduleBaseName;
                    if (targetSheet != null && allRoomSchedules.Any(s => s.Name.Equals(desiredName, StringComparison.OrdinalIgnoreCase)))
                    {
                        desiredName = $"{scheduleBaseName} ({targetSheet.SheetNumber})";
                    }
                    AssignUniqueViewName(doc, schedule, desiredName);
                }
                catch
                {
                    return null;
                }
            }

            // 3. Конфигурируем поля по ГОСТ 21.501-93 Форма 7, сортировку и фильтрацию
            try
            {
                ScheduleDefinition definition = schedule.Definition;
                IList<SchedulableField> schedulableFields = definition.GetSchedulableFields();

                // Колонки по ГОСТ 21.501-93 Форма 7:
                // Номер (20 мм), Наименование (65 мм), Площадь, м² (30 мм)
                ScheduleField? numberField = EnsureScheduleField(definition, schedulableFields, BuiltInParameter.ROOM_NUMBER, "Номер", 20.0);
                ScheduleField? nameField = EnsureScheduleField(definition, schedulableFields, BuiltInParameter.ROOM_NAME, "Наименование", 65.0);
                ScheduleField? areaField = EnsureScheduleField(definition, schedulableFields, BuiltInParameter.ROOM_AREA, "Площадь, м²", 30.0);

                // Поле "Уровень" (для поэтажной фильтрации, колонка скрыта IsHidden = true)
                ScheduleField? levelField = null;
                if (level != null)
                {
                    levelField = EnsureScheduleField(definition, schedulableFields, BuiltInParameter.ROOM_LEVEL_ID, "Уровень", 0.0, isHidden: true);
                    if (levelField == null)
                    {
                        levelField = EnsureScheduleField(definition, schedulableFields, BuiltInParameter.LEVEL_PARAM, "Уровень", 0.0, isHidden: true);
                    }
                }

                // Сортировка по возрастанию номера помещения (1, 2, 3...)
                if (numberField != null)
                {
                    definition.ClearSortGroupFields();
                    ScheduleSortGroupField sortField = new ScheduleSortGroupField(numberField.FieldId);
                    definition.AddSortGroupField(sortField);
                }

                // Очищаем старые фильтры
                definition.ClearFilters();

                // Фильтр 1: Площадь > 0.0 (полностью исключает "Not Placed" и "Not Enclosed" помещения!)
                if (areaField != null && definition.CanFilterByValue(areaField.FieldId))
                {
                    try
                    {
                        ScheduleFilter areaFilter = new ScheduleFilter(areaField.FieldId, ScheduleFilterType.GreaterThan, 0.0);
                        definition.AddFilter(areaFilter);
                    }
                    catch { }
                }

                // Фильтр 2: Уровень равен текущему этажу (показывает только помещения этого этажа!)
                if (level != null && levelField != null)
                {
                    try
                    {
                        ScheduleFilter levelFilter = new ScheduleFilter(levelField.FieldId, ScheduleFilterType.Equal, level.Id);
                        definition.AddFilter(levelFilter);
                    }
                    catch
                    {
                        try
                        {
                            ScheduleFilter levelFilter = new ScheduleFilter(levelField.FieldId, ScheduleFilterType.Equal, level.Name);
                            definition.AddFilter(levelFilter);
                        }
                        catch { }
                    }
                }

                doc.Regenerate();
                return schedule;
            }
            catch
            {
                return schedule;
            }
        }

        private static ScheduleField? EnsureScheduleField(
            ScheduleDefinition definition,
            IList<SchedulableField> schedulableFields,
            BuiltInParameter parameter,
            string columnHeader,
            double widthMm,
            bool isHidden = false)
        {
            ElementId paramId = new ElementId(parameter);

            ScheduleField? existingField = definition.GetFieldOrder()
                .Select(definition.GetField)
                .FirstOrDefault(f => f.ParameterId == paramId);

            if (existingField == null)
            {
                SchedulableField? match = schedulableFields.FirstOrDefault(f => f.ParameterId == paramId);
                if (match == null)
                {
                    return null;
                }

                existingField = definition.AddField(match);
            }

            if (existingField != null)
            {
                existingField.ColumnHeading = columnHeader;
                if (widthMm > 0.0)
                {
                    existingField.SheetColumnWidth = widthMm * MmToFeet;
                }
                existingField.IsHidden = isHidden;
            }

            return existingField;
        }

        private static ScheduleField? AddScheduleField(
            ScheduleDefinition definition,
            IList<SchedulableField> schedulableFields,
            BuiltInParameter parameter,
            string columnHeader,
            double widthMm)
        {
            return EnsureScheduleField(definition, schedulableFields, parameter, columnHeader, widthMm, isHidden: false);
        }

        private static bool IsPlaceableViewType(ViewType viewType) => viewType switch
        {
            ViewType.FloorPlan => true,
            ViewType.CeilingPlan => true,
            ViewType.EngineeringPlan => true,
            ViewType.AreaPlan => true,
            ViewType.Section => true,
            ViewType.Elevation => true,
            ViewType.Detail => true,
            ViewType.ThreeD => true,
            ViewType.DraftingView => true,
            ViewType.Legend => true,
            ViewType.Rendering => true,
            _ => false
        };

        private static int SelectStandardScale(
            double viewWidthMm,
            double viewHeightMm,
            double usableWidthMm,
            double usableHeightMm)
        {
            double requiredScaleX = viewWidthMm / usableWidthMm;
            double requiredScaleY = viewHeightMm / usableHeightMm;
            double requiredScale = Math.Max(requiredScaleX, requiredScaleY);

            foreach (int scale in StandardScales)
            {
                if (scale >= requiredScale)
                {
                    return scale;
                }
            }

            return StandardScales[^1];
        }

        private static void TrySetViewScale(View view, int targetScale)
        {
            if (view is View3D { IsPerspective: true })
            {
                return;
            }

            if (!View.IsValidViewScale(targetScale))
            {
                return;
            }

            Parameter? scaleParam = view.get_Parameter(BuiltInParameter.VIEW_SCALE);
            if (scaleParam is { IsReadOnly: false } && view.Scale != targetScale)
            {
                view.Scale = targetScale;
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

        private static void RemoveViewFromAnySheet(Document doc, ElementId viewId)
        {
            List<ElementId> vpsToDelete = [];
            foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate))
            {
                foreach (ElementId vpId in sheet.GetAllViewports())
                {
                    if (doc.GetElement(vpId) is Viewport vp && vp.ViewId == viewId)
                    {
                        vpsToDelete.Add(vpId);
                    }
                }
            }
            if (vpsToDelete.Count > 0)
            {
                try
                {
                    doc.Delete(vpsToDelete);
                    doc.Regenerate();
                }
                catch { }
            }
        }

        private static void RemoveViewportForView(Document doc, ViewSheet sheet, ElementId viewId)
        {
            List<ElementId> vpsToDelete = [];
            foreach (ElementId vpId in sheet.GetAllViewports())
            {
                if (doc.GetElement(vpId) is Viewport vp && vp.ViewId == viewId)
                {
                    vpsToDelete.Add(vpId);
                }
            }
            if (vpsToDelete.Count > 0)
            {
                try
                {
                    doc.Delete(vpsToDelete);
                    doc.Regenerate();
                }
                catch { }
            }
        }
    }
}
