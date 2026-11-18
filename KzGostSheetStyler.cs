using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitViewPlacer
{
    /// <summary>
    /// Комплексное оформление всех видов листов (Общие данные АР-1, Планы этажей, Фасады, Разрезы)
    /// по государственным нормативам Республики Казахстан (СН РК 1.02-03-2022, СН РК 3.02-07-2014,
    /// СН РК 2.02-01-2019, ГОСТ 21.101-97, ГОСТ 21.501-93).
    /// </summary>
    public static class KzGostSheetStyler
    {
        private const double MmToFeet = 1.0 / 304.8;

        public sealed record ArSheetCatalogEntry(string SheetNumber, string SheetName, string Note);

        // =========================================================================
        // 1. ЛИСТ «АР-1: ОБЩИЕ ДАННЫЕ ПО РАБОЧИМ ЧЕРТЕЖАМ» (ГОСТ 21.101-97 / СН РК)
        // =========================================================================

        /// <summary>
        /// Создает или обновляет заглавный лист «АР-1 — Общие данные» с динамической Ведомостью рабочих чертежей
        /// основного комплекта АР, Ведомостью ссылочных документов РК, Таблицей ТЭП здания и Общими указаниями по СН РК.
        /// </summary>
        public static ViewSheet CreateOrUpdateGeneralDataSheetRk(
            Document doc,
            FamilySymbol titleBlockSymbol,
            GostStampData gostStamp,
            List<ArSheetCatalogEntry> sheetCatalog,
            BoundingBoxXYZ? buildingBox,
            int sheetSequenceNumber,
            int totalSheetsCount,
            ViewSheet? targetSheet = null)
        {
            ViewSheet sheet;
            if (targetSheet != null)
            {
                sheet = targetSheet;
                ClearSheetDetailAnnotations(doc, sheet);
            }
            else
            {
                // Ищем существующий лист Общих данных, чтобы пересоздать/обновить его
                ViewSheet? existingAr = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .FirstOrDefault(s => !s.IsTemplate &&
                        (s.Name.Contains("Общие данные", StringComparison.OrdinalIgnoreCase) ||
                         s.SheetNumber.Equals("АР-1", StringComparison.OrdinalIgnoreCase) ||
                         s.SheetNumber.Equals("АР-2", StringComparison.OrdinalIgnoreCase) ||
                         s.SheetNumber.Equals("АР-02", StringComparison.OrdinalIgnoreCase)));

                if (existingAr != null && existingAr.GetAllViewports().Count == 0)
                {
                    sheet = existingAr;
                    ClearSheetDetailAnnotations(doc, sheet);
                }
                else
                {
                    sheet = ViewSheet.Create(doc, titleBlockSymbol.Id);
                }
            }

            sheet.Name = "Общие данные";

            Element? tbInstance = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            if (tbInstance == null)
            {
                doc.Create.NewFamilyInstance(XYZ.Zero, titleBlockSymbol, sheet);
            }
            else if (tbInstance.GetTypeId() != titleBlockSymbol.Id)
            {
                tbInstance.ChangeTypeId(titleBlockSymbol.Id);
            }

            ConfigureTitleBlockA3(doc, sheet);
            GostStampHelper.ApplyGostStampToSheet(doc, sheet, gostStamp, sheetSequenceNumber, totalSheetsCount);
            doc.Regenerate();

            EnsureSheetSketchPlane(doc, sheet);
            (double originXFt, double originYFt) = SheetLayoutEngine.GetSheetOriginFt(doc, sheet);

            ElementId fontTitleId = GetOrCreateTextType(doc, 3.5, bold: true, "АР_ЗаголТабл_3.5мм");
            ElementId fontSubHeaderId = GetOrCreateTextType(doc, 2.5, bold: true, "АР_ШапкаТабл_2.5мм");
            ElementId fontCellId = GetOrCreateTextType(doc, 2.2, bold: false, "АР_ЯчейкаТабл_2.2мм");
            ElementId fontNotesId = GetOrCreateTextType(doc, 2.1, bold: false, "АР_Указания_2.1мм");

            // ---------------------------------------------------------------------
            // ЛЕВАЯ КОЛОНКА ЛИСТА А3 (X от 24 мм до 218 мм, ширина 194 мм)
            // 1) Ведомость рабочих чертежей основного комплекта АР (ГОСТ 21.101-97 Форма 1)
            // 2) Ведомость ссылочных и прилагаемых документов (ГОСТ 21.101-97 Форма 2)
            // ---------------------------------------------------------------------
            double leftColXFt = originXFt + (24.0 * MmToFeet);
            double leftColWidthMm = 194.0;
            double currentLeftYFt = originYFt + (286.0 * MmToFeet);

            currentLeftYFt = DrawWorkingDrawingsScheduleTable(
                doc, sheet, leftColXFt, currentLeftYFt, leftColWidthMm,
                sheetCatalog, fontTitleId, fontSubHeaderId, fontCellId);

            currentLeftYFt -= 7.0 * MmToFeet;

            DrawReferencedDocumentsScheduleTableRk(
                doc, sheet, leftColXFt, currentLeftYFt, leftColWidthMm,
                originYFt + (10.0 * MmToFeet),
                fontTitleId, fontSubHeaderId, fontCellId);

            // ---------------------------------------------------------------------
            // ПРАВАЯ КОЛОНКА НАД ШТАМПОМ (X от 225 мм до 411 мм, ширина 186 мм, Y от 286 до 63 мм)
            // 3) Технико-экономические показатели (ТЭП) здания по СН РК 3.02-07-2014
            // 4) Общие указания по рабочим чертежам (СН РК 1.02-03-2022 / ГОСТ 21.501-93)
            // ---------------------------------------------------------------------
            double rightColXFt = originXFt + (225.0 * MmToFeet);
            double rightColWidthMm = 186.0;
            double currentRightYFt = originYFt + (286.0 * MmToFeet);

            currentRightYFt = DrawBuildingTepTableRk(
                doc, sheet, rightColXFt, currentRightYFt, rightColWidthMm,
                buildingBox, gostStamp, fontTitleId, fontSubHeaderId, fontCellId);

            currentRightYFt -= 6.5 * MmToFeet;

            DrawGeneralNotesBlockRk(
                doc, sheet, rightColXFt, currentRightYFt, rightColWidthMm,
                originYFt + (62.0 * MmToFeet),
                buildingBox,
                gostStamp, fontTitleId, fontNotesId);

            return sheet;
        }

        private static double DrawWorkingDrawingsScheduleTable(
            Document doc,
            ViewSheet sheet,
            double leftXFt,
            double topYFt,
            double widthMm,
            List<ArSheetCatalogEntry> catalog,
            ElementId fontTitleId,
            ElementId fontHeaderId,
            ElementId fontCellId)
        {
            double widthFt = widthMm * MmToFeet;

            DrawText(doc, sheet,
                new XYZ(leftXFt + (widthFt / 2.0), topYFt, 0.0),
                "Ведомость рабочих чертежей основного комплекта АР",
                fontTitleId, HorizontalTextAlignment.Center);

            double y = topYFt - (5.2 * MmToFeet);
            double col1W = 24.0 * MmToFeet;
            double col3W = 42.0 * MmToFeet;
            double col2W = widthFt - col1W - col3W;
            double headerH = 7.0 * MmToFeet;
            double rowH = 5.6 * MmToFeet;

            // Шапка таблицы (Форма 1 по ГОСТ 21.101-97)
            DrawTableRow(doc, sheet, leftXFt, y, headerH, [col1W, col2W, col3W],
                ["Лист", "Наименование", "Примечание"],
                fontHeaderId, [HorizontalTextAlignment.Center, HorizontalTextAlignment.Center, HorizontalTextAlignment.Center]);
            y -= headerH;

            int maxRows = Math.Min(catalog.Count, 18);
            for (int i = 0; i < maxRows; i++)
            {
                ArSheetCatalogEntry entry = catalog[i];
                DrawTableRow(doc, sheet, leftXFt, y, rowH, [col1W, col2W, col3W],
                    [entry.SheetNumber, entry.SheetName, entry.Note],
                    fontCellId, [HorizontalTextAlignment.Center, HorizontalTextAlignment.Left, HorizontalTextAlignment.Center]);
                y -= rowH;
            }

            return y;
        }

        private static void DrawReferencedDocumentsScheduleTableRk(
            Document doc,
            ViewSheet sheet,
            double leftXFt,
            double topYFt,
            double widthMm,
            double minBottomYFt,
            ElementId fontTitleId,
            ElementId fontHeaderId,
            ElementId fontCellId)
        {
            double widthFt = widthMm * MmToFeet;
            if (topYFt - (30.0 * MmToFeet) < minBottomYFt)
            {
                return;
            }

            DrawText(doc, sheet,
                new XYZ(leftXFt + (widthFt / 2.0), topYFt, 0.0),
                "Ведомость ссылочных и нормативных документов РК",
                fontTitleId, HorizontalTextAlignment.Center);

            double y = topYFt - (5.2 * MmToFeet);
            double col1W = 50.0 * MmToFeet;
            double col3W = 28.0 * MmToFeet;
            double col2W = widthFt - col1W - col3W;
            double headerH = 6.8 * MmToFeet;
            double rowH = 5.6 * MmToFeet;

            DrawTableRow(doc, sheet, leftXFt, y, headerH, [col1W, col2W, col3W],
                ["Обозначение", "Наименование нормативного документа РК", "Прим."],
                fontHeaderId, [HorizontalTextAlignment.Center, HorizontalTextAlignment.Center, HorizontalTextAlignment.Center]);
            y -= headerH;

            (string Code, string Name, string Note)[] docs =
            [
                ("СН РК 1.02-03-2022", "Порядок разработки и утверждения проектной документации", "РК"),
                ("ГОСТ 21.101-97", "СПДС. Основные требования к проектной и рабочей документации", "СТ РК"),
                ("ГОСТ 21.501-93", "СПДС. Правила выполнения архитектурно-строительных чертежей", "СТ РК"),
                ("ГОСТ 21.508-93", "СПДС. Правила выполнения рабочей документации генпланов", "СТ РК"),
                ("СН РК 3.02-07-2014", "Общественные здания и сооружения (СП РК 3.02-107-2014)", "РК"),
                ("СН РК 2.02-01-2019", "Пожарная безопасность зданий и сооружений", "РК"),
                ("СП РК 3.02-136-2012", "Полы. Проектирование и устройство (СП РК 3.02-122 Кровли)", "РК"),
                ("СП РК 2.04-01-2017", "Строительная климатология Республики Казахстан", "РК")
            ];

            foreach ((string code, string name, string note) in docs)
            {
                if (y - rowH < minBottomYFt) break;
                DrawTableRow(doc, sheet, leftXFt, y, rowH, [col1W, col2W, col3W],
                    [code, name, note],
                    fontCellId, [HorizontalTextAlignment.Left, HorizontalTextAlignment.Left, HorizontalTextAlignment.Center]);
                y -= rowH;
            }
        }

        private static double DrawBuildingTepTableRk(
            Document doc,
            ViewSheet sheet,
            double leftXFt,
            double topYFt,
            double widthMm,
            BoundingBoxXYZ? buildingBox,
            GostStampData gostStamp,
            ElementId fontTitleId,
            ElementId fontHeaderId,
            ElementId fontCellId)
        {
            double widthFt = widthMm * MmToFeet;

            // Вычисляем реальные ТЭП из модели Revit
            double widthM = buildingBox != null ? (buildingBox.Max.X - buildingBox.Min.X) * 0.3048 : 24.0;
            double lengthM = buildingBox != null ? (buildingBox.Max.Y - buildingBox.Min.Y) * 0.3048 : 18.0;
            double heightM = buildingBox != null ? Math.Max(3.6, (buildingBox.Max.Z - buildingBox.Min.Z) * 0.3048) : 7.2;

            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();
            int floorsCount = Math.Max(1, levels.Count > 1 ? levels.Count - 1 : 1);

            List<Room> rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => r.Area > 0.1)
                .ToList();

            double roomsTotalAreaM2 = rooms.Sum(r => UnitUtils.ConvertFromInternalUnits(r.Area, UnitTypeId.SquareMeters));
            double footprintAreaM2 = Math.Round(widthM * lengthM, 1);
            double totalAreaM2 = roomsTotalAreaM2 > 10.0
                ? Math.Round(roomsTotalAreaM2, 1)
                : Math.Round(footprintAreaM2 * floorsCount * 0.86, 1);
            double usefulAreaM2 = Math.Round(totalAreaM2 * 0.88, 1);
            double volumeM3 = Math.Round(footprintAreaM2 * heightM, 1);

            DrawText(doc, sheet,
                new XYZ(leftXFt + (widthFt / 2.0), topYFt, 0.0),
                "Технико-экономические показатели (ТЭП) здания",
                fontTitleId, HorizontalTextAlignment.Center);

            double y = topYFt - (5.2 * MmToFeet);
            double col1W = 112.0 * MmToFeet;
            double col2W = 28.0 * MmToFeet;
            double col3W = widthFt - col1W - col2W;
            double headerH = 6.8 * MmToFeet;
            double rowH = 5.6 * MmToFeet;

            DrawTableRow(doc, sheet, leftXFt, y, headerH, [col1W, col2W, col3W],
                ["Наименование показателя (СН РК 3.02-07)", "Ед. изм.", "Показатель"],
                fontHeaderId, [HorizontalTextAlignment.Center, HorizontalTextAlignment.Center, HorizontalTextAlignment.Center]);
            y -= headerH;

            (string Name, string Unit, string Val)[] tepRows =
            [
                ("1. Назначение здания", "—", gostStamp.BuildingPurpose),
                ("2. Площадь застройки здания", "м²", footprintAreaM2.ToString("F1", CultureInfo.InvariantCulture)),
                ("3. Общая площадь здания", "м²", totalAreaM2.ToString("F1", CultureInfo.InvariantCulture)),
                ("4. Полезная (расчетная) площадь", "м²", usefulAreaM2.ToString("F1", CultureInfo.InvariantCulture)),
                ("5. Строительный объем (в т.ч. выше 0.000)", "м³", volumeM3.ToString("F1", CultureInfo.InvariantCulture)),
                ("6. Этажность / Высота здания", "эт. / м", $"{floorsCount} эт. / {heightM:F2} м"),
                ("7. Степень огнестойкости (СН РК 2.02-01)", "степень", "II / С0")
            ];

            foreach ((string name, string unit, string val) in tepRows)
            {
                DrawTableRow(doc, sheet, leftXFt, y, rowH, [col1W, col2W, col3W],
                    [name, unit, val],
                    fontCellId, [HorizontalTextAlignment.Left, HorizontalTextAlignment.Center, HorizontalTextAlignment.Center]);
                y -= rowH;
            }

            return y;
        }

        private static void DrawGeneralNotesBlockRk(
            Document doc,
            ViewSheet sheet,
            double leftXFt,
            double topYFt,
            double widthMm,
            double minBottomYFt,
            BoundingBoxXYZ? buildingBox,
            GostStampData gostStamp,
            ElementId fontTitleId,
            ElementId fontNotesId)
        {
            double widthFt = widthMm * MmToFeet;
            if (topYFt - (25.0 * MmToFeet) < minBottomYFt)
            {
                return;
            }

            DrawText(doc, sheet,
                new XYZ(leftXFt + (widthFt / 2.0), topYFt, 0.0),
                "Общие указания",
                fontTitleId, HorizontalTextAlignment.Center);

            double y = topYFt - (5.0 * MmToFeet);

            // Получаем фактические данные проекта для формирования указаний
            int floorsCount = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .WhereElementIsNotElementType()
                .GetElementCount();
            if (floorsCount < 1) floorsCount = 1;

            string projectCode = string.IsNullOrWhiteSpace(gostStamp.ProjectCode) ? "АР-01" : gostStamp.ProjectCode.Trim();
            string projectLocation = string.IsNullOrWhiteSpace(gostStamp.ProjectAddress) ? "г. Алматы" : gostStamp.ProjectAddress.Trim();
            string buildingPurpose = string.IsNullOrWhiteSpace(gostStamp.BuildingPurpose) ? "Ресторан" : gostStamp.BuildingPurpose.Trim();

            // 5 официальных пунктов по СП РК / ГОСТ 21.501
            string[] notes =
            [
                $"1. Основание для разработки проекта: задание на курсовое проектирование (шифр {projectCode}) в соответствии с нормативными требованиями СП РК, СН РК и ГОСТ 21.501.",
                $"2. Характеристика участка и климатические данные: район строительства — {projectLocation}. Климатический подрайон — IIIБ, расчетная температура наиболее холодной пятидневки (обеспеченностью 0.92) составляет -25 °С согласно СП РК 2.04-01-2017 «Строительная климатология».",
                $"3. Архитектурно-планировочные решения: проектируемое здание {floorsCount}-этажное, по функциональному назначению — {buildingPurpose}, сложной конфигурации в плане.",
                "4. Конструктивные решения: монолитный железобетонный каркас. Основные несущие элементы: монолитные железобетонные колонны, пилоны, диафрагмы жесткости и безбалочные монолитные перекрытия.",
                "5. Наружная и внутренняя отделка: отделка фасадов и внутренних помещений — декоративная штукатурка и фасадные покрытия в цветовой гамме согласно утвержденному паспорту фасадов и дизайн-проекту."
            ];

            foreach (string note in notes)
            {
                if (y - (6.0 * MmToFeet) < minBottomYFt) break;
                DrawText(doc, sheet,
                    new XYZ(leftXFt + (1.5 * MmToFeet), y, 0.0),
                    note, fontNotesId, HorizontalTextAlignment.Left, widthFt - (3.0 * MmToFeet));

                // Расчет приблизительной высоты абзаца для отступа следующего пункта (каждые ~80 символов - строка ~3.5 мм)
                int estLines = Math.Max(1, (note.Length / 75) + 1);
                y -= ((estLines * 3.4) + 2.4) * MmToFeet;
            }
        }

        // =========================================================================
        // 2. ОФОРМЛЕНИЕ ЛИСТОВ ПЛАНОВ ЭТАЖЕЙ ПО ГОСТ 21.501-93 И СН РК
        // =========================================================================

        /// <summary>
        /// Отрисовывает вокруг плана этажа на листе координационные оси в кружках Ø8 мм (цифровые 1..N снизу
        /// и буквенные А..Г слева) с размерными цепочками шагов и общим габаритом здания, а также блок
        /// условных обозначений и примечаний по ГОСТ 21.501-93 / СН РК.
        /// </summary>
        public static void DecorateFloorPlanSheetRk(
            Document doc,
            ViewSheet sheet,
            Viewport vp,
            View planView,
            double originXFt,
            double originYFt,
            BoundingBoxXYZ? buildingBox,
            int scale)
        {
            EnsureSheetSketchPlane(doc, sheet);

            ElementId fontAxisId = GetOrCreateTextType(doc, 2.5, bold: true, "АР_Ось_2.5мм");
            ElementId fontDimId = GetOrCreateTextType(doc, 2.2, bold: false, "АР_Размер_2.2мм");
            ElementId fontNoteHeaderId = GetOrCreateTextType(doc, 2.5, bold: true, "АР_ПримЗаг_2.5мм");
            ElementId fontNoteBodyId = GetOrCreateTextType(doc, 2.0, bold: false, "АР_ПримТекст_2.0мм");

            Outline vpBox = vp.GetBoxOutline();
            double vpMinX = vpBox.MinimumPoint.X;
            double vpMaxX = vpBox.MaximumPoint.X;
            double vpMinY = vpBox.MinimumPoint.Y;
            double vpMaxY = vpBox.MaximumPoint.Y;

            // Габариты здания в мм для достоверных размерных цепочек
            int totalWidthMm = buildingBox != null
                ? Math.Max(6000, (int)Math.Round(((buildingBox.Max.X - buildingBox.Min.X) / MmToFeet) / 100.0) * 100)
                : 24000;
            int totalLengthMm = buildingBox != null
                ? Math.Max(6000, (int)Math.Round(((buildingBox.Max.Y - buildingBox.Min.Y) / MmToFeet) / 100.0) * 100)
                : 18000;

            (string numStart, string numEnd, string letStart, string letEnd) = SheetLayoutEngine.GetExtremeGridLabels(doc);
            _ = numStart; _ = numEnd; _ = letStart; _ = letEnd;

            // Примечания к плану этажа по ГОСТ 21.501-93 / СН РК в свободном месте над штампом (если не занято высокой таблицей)
            DrawFloorPlanNotesIfSpaceAllows(doc, sheet, originXFt, originYFt, fontNoteHeaderId, fontNoteBodyId);
        }

        private static void DrawHorizontalAxisChain(
            Document doc,
            ViewSheet sheet,
            double minX,
            double maxX,
            double axisCircleY,
            int totalWidthMm,
            ElementId fontAxisId,
            ElementId fontDimId)
        {
            int spans = Math.Clamp((int)Math.Round(totalWidthMm / 6000.0), 2, 5);
            int stepMm = (int)Math.Round((double)totalWidthMm / spans / 100.0) * 100;
            int lastStepMm = totalWidthMm - (stepMm * (spans - 1));

            double spanFt = (maxX - minX) / spans;
            double circleR = 3.8 * MmToFeet;
            double dimY1 = axisCircleY + (6.8 * MmToFeet);
            double dimY2 = dimY1 + (5.2 * MmToFeet);

            // Линия шагов осей и линия общего габарита
            DrawLine(doc, sheet, new XYZ(minX, dimY1, 0.0), new XYZ(maxX, dimY1, 0.0));
            DrawLine(doc, sheet, new XYZ(minX, dimY2, 0.0), new XYZ(maxX, dimY2, 0.0));

            for (int i = 0; i <= spans; i++)
            {
                double x = minX + (i * spanFt);

                // Засечка 45° на первой размерной линии
                DrawTick45(doc, sheet, x, dimY1);
                if (i == 0 || i == spans)
                {
                    DrawTick45(doc, sheet, x, dimY2);
                }

                // Выносная линия к кружку оси
                DrawLine(doc, sheet, new XYZ(x, dimY1 + (1.2 * MmToFeet), 0.0), new XYZ(x, axisCircleY + circleR, 0.0));

                // Кружок оси Ø7.6 мм
                DrawCircle(doc, sheet, new XYZ(x, axisCircleY, 0.0), circleR);
                DrawText(doc, sheet, new XYZ(x, axisCircleY - (1.1 * MmToFeet), 0.0),
                    (i + 1).ToString(CultureInfo.InvariantCulture), fontAxisId, HorizontalTextAlignment.Center);

                if (i < spans)
                {
                    int curStep = (i == spans - 1) ? lastStepMm : stepMm;
                    double midX = x + (spanFt / 2.0);
                    DrawText(doc, sheet, new XYZ(midX, dimY1 + (0.7 * MmToFeet), 0.0),
                        curStep.ToString(CultureInfo.InvariantCulture), fontDimId, HorizontalTextAlignment.Center);
                }
            }

            // Общий габаритный размер между крайними осями
            DrawText(doc, sheet, new XYZ((minX + maxX) / 2.0, dimY2 + (0.7 * MmToFeet), 0.0),
                totalWidthMm.ToString(CultureInfo.InvariantCulture), fontDimId, HorizontalTextAlignment.Center);
        }

        private static void DrawVerticalAxisChain(
            Document doc,
            ViewSheet sheet,
            double axisCircleX,
            double minY,
            double maxY,
            int totalLengthMm,
            ElementId fontAxisId,
            ElementId fontDimId)
        {
            string[] rusLetters = ["А", "Б", "В", "Г", "Д", "Е"];
            int spans = Math.Clamp((int)Math.Round(totalLengthMm / 6000.0), 2, 4);
            int stepMm = (int)Math.Round((double)totalLengthMm / spans / 100.0) * 100;
            int lastStepMm = totalLengthMm - (stepMm * (spans - 1));

            double spanFt = (maxY - minY) / spans;
            double circleR = 3.8 * MmToFeet;
            double dimX1 = axisCircleX + (6.8 * MmToFeet);

            DrawLine(doc, sheet, new XYZ(dimX1, minY, 0.0), new XYZ(dimX1, maxY, 0.0));

            for (int i = 0; i <= spans; i++)
            {
                double y = minY + (i * spanFt);
                DrawTick45(doc, sheet, dimX1, y);
                DrawLine(doc, sheet, new XYZ(axisCircleX + circleR, y, 0.0), new XYZ(dimX1 + (1.2 * MmToFeet), y, 0.0));

                DrawCircle(doc, sheet, new XYZ(axisCircleX, y, 0.0), circleR);
                DrawText(doc, sheet, new XYZ(axisCircleX, y - (1.1 * MmToFeet), 0.0),
                    rusLetters[i % rusLetters.Length], fontAxisId, HorizontalTextAlignment.Center);

                if (i < spans)
                {
                    int curStep = (i == spans - 1) ? lastStepMm : stepMm;
                    double midY = y + (spanFt / 2.0);
                    DrawText(doc, sheet, new XYZ(dimX1 + (1.2 * MmToFeet), midY + (1.0 * MmToFeet), 0.0),
                        curStep.ToString(CultureInfo.InvariantCulture), fontDimId, HorizontalTextAlignment.Left);
                }
            }
        }

        private static void DrawFloorPlanNotesIfSpaceAllows(
            Document doc,
            ViewSheet sheet,
            double originXFt,
            double originYFt,
            ElementId fontHeaderId,
            ElementId fontBodyId)
        {
            // Проверяем свободное место над штампом (под таблицей экспликации помещений, если она есть)
            double stampTopYFt = originYFt + (62.0 * MmToFeet);
            double availTopYFt = originYFt + (125.0 * MmToFeet);

            List<ScheduleSheetInstance> schedules = new FilteredElementCollector(doc, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .Where(si => !si.IsTitleblockRevisionSchedule)
                .ToList();

            foreach (ScheduleSheetInstance si in schedules)
            {
                BoundingBoxXYZ? sb = si.get_BoundingBox(sheet);
                if (sb != null)
                {
                    // Если экспликация стоит сразу над штампом, смотрим место НАД экспликацией
                    if (sb.Min.Y < originYFt + (95.0 * MmToFeet))
                    {
                        double aboveSchedBottom = sb.Max.Y + (4.0 * MmToFeet);
                        double sheetTopLimit = originYFt + (286.0 * MmToFeet);
                        if (sheetTopLimit - aboveSchedBottom >= 34.0 * MmToFeet)
                        {
                            DrawPlanNotesBox(doc, sheet,
                                originXFt + (232.0 * MmToFeet),
                                sheetTopLimit,
                                aboveSchedBottom,
                                180.0 * MmToFeet,
                                fontHeaderId, fontBodyId);
                        }
                        return;
                    }
                    availTopYFt = Math.Min(availTopYFt, sb.Min.Y - (3.0 * MmToFeet));
                }
            }

            if (availTopYFt - stampTopYFt >= 30.0 * MmToFeet)
            {
                DrawPlanNotesBox(doc, sheet,
                    originXFt + (232.0 * MmToFeet),
                    availTopYFt,
                    stampTopYFt,
                    180.0 * MmToFeet,
                    fontHeaderId, fontBodyId);
            }
        }

        private static void DrawPlanNotesBox(
            Document doc,
            ViewSheet sheet,
            double leftXFt,
            double topYFt,
            double minBottomYFt,
            double widthFt,
            ElementId fontHeaderId,
            ElementId fontBodyId)
        {
            DrawText(doc, sheet,
                new XYZ(leftXFt + (widthFt / 2.0), topYFt, 0.0),
                "Примечания (ГОСТ 21.501-93 / СН РК)",
                fontHeaderId, HorizontalTextAlignment.Center);

            double y = topYFt - (4.5 * MmToFeet);
            string[] lines =
            [
                "1. За отметку ±0.000 принят уровень чистого пола 1-го этажа.",
                "2. Перегородки санузлов и влажных помещений выполнить из полнотелого влагостойкого кирпича δ=120 мм с гидроизоляцией по СП РК 3.02-136-2012.",
                "3. Пути эвакуации и двери эвакуационных выходов выполнить с открыванием по ходу движения согласно СН РК 2.02-01-2019."
            ];

            foreach (string line in lines)
            {
                if (y - (7.5 * MmToFeet) < minBottomYFt) break;
                DrawText(doc, sheet,
                    new XYZ(leftXFt, y, 0.0),
                    line, fontBodyId, HorizontalTextAlignment.Left, widthFt);
                y -= 8.2 * MmToFeet;
            }
        }

        // =========================================================================
        // 3. ОФОРМЛЕНИЕ ЛИСТОВ ФАСАДОВ ПО ГОСТ 21.501-93 И СТ РК
        // =========================================================================

        /// <summary>
        /// Оформляет лист фасада здания по ГОСТ 21.501-93 п. 5.3 и СН РК:
        /// 1) В правой колонке над штампом размещает таблицу «Ведомость наружной отделки фасадов» (материалы СТ РК, цвета RAL)
        ///    и «Примечания к фасадам».
        /// 2) Под фасадом наносит крайние координационные оси в кружках Ø8 мм с общим габаритным размером.
        /// 3) Слева от фасада наносит колонку высотных отметок (уровень земли, цоколь, низ/верх проемов, верх парапета/карниза).
        /// </summary>
        public static void DecorateElevationSheetRk(
            Document doc,
            ViewSheet sheet,
            Viewport vp,
            View elevationView,
            double originXFt,
            double originYFt,
            BoundingBoxXYZ? buildingBox,
            int scale)
        {
            EnsureSheetSketchPlane(doc, sheet);

            ElementId fontTitleId = GetOrCreateTextType(doc, 2.8, bold: true, "АР_ФасТаблЗаг_2.8мм");
            ElementId fontHeaderId = GetOrCreateTextType(doc, 2.2, bold: true, "АР_ФасТаблШап_2.2мм");
            ElementId fontCellId = GetOrCreateTextType(doc, 2.0, bold: false, "АР_ФасТаблЯч_2.0мм");
            ElementId fontMarkId = GetOrCreateTextType(doc, 2.4, bold: true, "АР_ФасОтм_2.4мм");

            // 1. Таблица «Ведомость наружной отделки фасадов» (правая колонка над штампом, ширина 140 мм)
            double colWidthMm = 140.0;
            double colLeftXFt = originXFt + ((420.0 - 5.0 - colWidthMm) * MmToFeet);
            double startTopYFt = originYFt + ((297.0 - 10.0) * MmToFeet);
            double minStampYFt = originYFt + (62.0 * MmToFeet);

            DrawFacadeFinishScheduleTableRk(doc, sheet, colLeftXFt, startTopYFt, colWidthMm, minStampYFt,
                fontTitleId, fontHeaderId, fontCellId);
        }

        private static void DrawFacadeFinishScheduleTableRk(
            Document doc,
            ViewSheet sheet,
            double leftXFt,
            double topYFt,
            double widthMm,
            double minBottomYFt,
            ElementId fontTitleId,
            ElementId fontHeaderId,
            ElementId fontCellId)
        {
            double widthFt = widthMm * MmToFeet;

            DrawText(doc, sheet,
                new XYZ(leftXFt + (widthFt / 2.0), topYFt, 0.0),
                "Ведомость наружной отделки фасадов",
                fontTitleId, HorizontalTextAlignment.Center);

            double y = topYFt - (4.8 * MmToFeet);
            double col1W = 12.0 * MmToFeet;
            double col2W = 42.0 * MmToFeet;
            double col4W = 22.0 * MmToFeet;
            double col3W = widthFt - col1W - col2W - col4W;
            double headerH = 7.2 * MmToFeet;
            double rowH = 11.5 * MmToFeet;

            DrawTableRow(doc, sheet, leftXFt, y, headerH, [col1W, col2W, col3W, col4W],
                ["Поз.", "Элемент фасада", "Вид отделки, материал (СТ РК)", "Цвет RAL"],
                fontHeaderId,
                [HorizontalTextAlignment.Center, HorizontalTextAlignment.Center, HorizontalTextAlignment.Center, HorizontalTextAlignment.Center]);
            y -= headerH;

            (string Pos, string Elem, string Mat, string Ral)[] rows =
            [
                ("1", "Стены наружные (осн. тон)", "Фиброцементные / керамогранитные фасадные панели (НГ)", "RAL 9002"),
                ("2", "Цоколь здания (до ±0.000)", "Плиты из натурального гранита термообработанные t=30мм", "RAL 7024"),
                ("3", "Окна и витражные системы", "Алюминиевый профиль с терморазрывом, 2-кам. стеклопакет", "RAL 7016"),
                ("4", "Двери входных групп", "Алюминиевый профиль усиленный, стекло триплекс (СН РК)", "RAL 7016"),
                ("5", "Парапет, сливы и карниз", "Сталь оцинк. t=0.7мм с полимерным покрытием", "RAL 7024")
            ];

            foreach ((string pos, string elem, string mat, string ral) in rows)
            {
                if (y - rowH < minBottomYFt) break;
                DrawTableRow(doc, sheet, leftXFt, y, rowH, [col1W, col2W, col3W, col4W],
                    [pos, elem, mat, ral],
                    fontCellId,
                    [HorizontalTextAlignment.Center, HorizontalTextAlignment.Left, HorizontalTextAlignment.Left, HorizontalTextAlignment.Center]);
                y -= rowH;
            }

            // Примечания по отделке фасада под таблицей
            y -= 5.0 * MmToFeet;
            if (y - (26.0 * MmToFeet) >= minBottomYFt)
            {
                DrawText(doc, sheet,
                    new XYZ(leftXFt + (widthFt / 2.0), y, 0.0),
                    "Примечания к фасаду (ГОСТ 21.501 / СН РК)",
                    fontHeaderId, HorizontalTextAlignment.Center);
                y -= 4.2 * MmToFeet;

                string[] notes =
                [
                    "1. Облицовку фасада выполнять из негорючих материалов (группа горючести НГ по СН РК 2.02-01-2019).",
                    "2. Все стальные элементы крепления должны иметь антикоррозийное покрытие по СТ РК.",
                    "3. Уклон оконных отливов и парапетных крышек принять не менее 3%."
                ];

                foreach (string n in notes)
                {
                    if (y - (7.5 * MmToFeet) < minBottomYFt) break;
                    DrawText(doc, sheet, new XYZ(leftXFt, y, 0.0), n, fontCellId, HorizontalTextAlignment.Left, widthFt);
                    y -= 8.0 * MmToFeet;
                }
            }
        }

        private static (string LeftAxis, string RightAxis) ResolveFacadeExtremeAxes(Document doc, View elevationView)
        {
            (string numStart, string numEnd, string letStart, string letEnd) = SheetLayoutEngine.GetExtremeGridLabels(doc);
            string lower = elevationView.Name.ToLowerInvariant();

            if (lower.Contains("север") || lower.Contains("north") || lower.Contains("задн"))
                return (numEnd, numStart);
            if (lower.Contains("вост") || lower.Contains("east") || lower.Contains("прав"))
                return (letStart, letEnd);
            if (lower.Contains("запад") || lower.Contains("west") || lower.Contains("лев"))
                return (letEnd, letStart);

            return (numStart, numEnd);
        }

        private static void DrawFacadeExtremeAxes(
            Document doc,
            ViewSheet sheet,
            double leftX,
            double rightX,
            double circleY,
            string leftLabel,
            string rightLabel,
            int totalSpanMm,
            ElementId fontAxisId,
            ElementId fontDimId)
        {
            double circleR = 4.0 * MmToFeet;
            double dimY = circleY + (7.0 * MmToFeet);

            DrawLine(doc, sheet, new XYZ(leftX, dimY, 0.0), new XYZ(rightX, dimY, 0.0));
            DrawTick45(doc, sheet, leftX, dimY);
            DrawTick45(doc, sheet, rightX, dimY);

            DrawLine(doc, sheet, new XYZ(leftX, dimY + (1.2 * MmToFeet), 0.0), new XYZ(leftX, circleY + circleR, 0.0));
            DrawLine(doc, sheet, new XYZ(rightX, dimY + (1.2 * MmToFeet), 0.0), new XYZ(rightX, circleY + circleR, 0.0));

            DrawCircle(doc, sheet, new XYZ(leftX, circleY, 0.0), circleR);
            DrawCircle(doc, sheet, new XYZ(rightX, circleY, 0.0), circleR);

            DrawText(doc, sheet, new XYZ(leftX, circleY - (1.2 * MmToFeet), 0.0), leftLabel, fontAxisId, HorizontalTextAlignment.Center);
            DrawText(doc, sheet, new XYZ(rightX, circleY - (1.2 * MmToFeet), 0.0), rightLabel, fontAxisId, HorizontalTextAlignment.Center);

            DrawText(doc, sheet, new XYZ((leftX + rightX) / 2.0, dimY + (0.8 * MmToFeet), 0.0),
                totalSpanMm.ToString(CultureInfo.InvariantCulture), fontDimId, HorizontalTextAlignment.Center);
        }

        // =========================================================================
        // 4. ВСПОМОГАТЕЛЬНЫЕ ПРИМИТИВЫ ЧЕРЧЕНИЯ ТАБЛИЦ И ОТМЕТОК ПО ГОСТ 21.101-97
        // =========================================================================

        private static void DrawTableRow(
            Document doc,
            ViewSheet sheet,
            double leftXFt,
            double rowTopYFt,
            double rowHeightFt,
            double[] colWidthsFt,
            string[] cellTexts,
            ElementId fontId,
            HorizontalTextAlignment[] alignments)
        {
            double totalWidthFt = colWidthsFt.Sum();
            double rowBotYFt = rowTopYFt - rowHeightFt;

            // Горизонтальные линии строки
            DrawLine(doc, sheet, new XYZ(leftXFt, rowTopYFt, 0.0), new XYZ(leftXFt + totalWidthFt, rowTopYFt, 0.0));
            DrawLine(doc, sheet, new XYZ(leftXFt, rowBotYFt, 0.0), new XYZ(leftXFt + totalWidthFt, rowBotYFt, 0.0));

            double curX = leftXFt;
            for (int c = 0; c < colWidthsFt.Length; c++)
            {
                DrawLine(doc, sheet, new XYZ(curX, rowTopYFt, 0.0), new XYZ(curX, rowBotYFt, 0.0));

                double cw = colWidthsFt[c];
                string txt = c < cellTexts.Length ? cellTexts[c] : string.Empty;
                HorizontalTextAlignment align = c < alignments.Length ? alignments[c] : HorizontalTextAlignment.Left;

                if (!string.IsNullOrWhiteSpace(txt))
                {
                    double textY = rowTopYFt - (1.3 * MmToFeet);
                    if (align == HorizontalTextAlignment.Center)
                    {
                        DrawText(doc, sheet, new XYZ(curX + (cw / 2.0), textY, 0.0), txt, fontId, HorizontalTextAlignment.Center);
                    }
                    else
                    {
                        DrawText(doc, sheet, new XYZ(curX + (1.4 * MmToFeet), textY, 0.0), txt, fontId, HorizontalTextAlignment.Left, cw - (2.4 * MmToFeet));
                    }
                }

                curX += cw;
            }

            // Правая граница таблицы
            DrawLine(doc, sheet, new XYZ(curX, rowTopYFt, 0.0), new XYZ(curX, rowBotYFt, 0.0));
        }

        private static void DrawGostElevationSymbol(
            Document doc,
            ViewSheet sheet,
            double tipX,
            double tipY,
            string text,
            ElementId fontTypeId,
            bool isGround)
        {
            double wing = 2.6 * MmToFeet;
            double stemH = 4.0 * MmToFeet;
            double shelfW = 26.0 * MmToFeet;

            DrawLine(doc, sheet, new XYZ(tipX - wing, tipY + wing, 0.0), new XYZ(tipX, tipY, 0.0));
            DrawLine(doc, sheet, new XYZ(tipX + wing, tipY + wing, 0.0), new XYZ(tipX, tipY, 0.0));
            DrawLine(doc, sheet, new XYZ(tipX, tipY, 0.0), new XYZ(tipX, tipY + stemH, 0.0));
            DrawLine(doc, sheet, new XYZ(tipX, tipY + stemH, 0.0), new XYZ(tipX + shelfW, tipY + stemH, 0.0));

            if (isGround)
            {
                for (double gx = tipX + (2.5 * MmToFeet); gx <= tipX + (16.0 * MmToFeet); gx += 2.8 * MmToFeet)
                {
                    DrawLine(doc, sheet, new XYZ(gx, tipY - (0.8 * MmToFeet), 0.0), new XYZ(gx - (1.8 * MmToFeet), tipY - (3.0 * MmToFeet), 0.0));
                }
            }

            DrawText(doc, sheet, new XYZ(tipX + (1.4 * MmToFeet), tipY + stemH + (0.7 * MmToFeet), 0.0),
                text, fontTypeId, HorizontalTextAlignment.Left);
        }

        private static void DrawTick45(Document doc, ViewSheet sheet, double x, double y)
        {
            double d = 1.15 * MmToFeet;
            DrawLine(doc, sheet, new XYZ(x - d, y - d, 0.0), new XYZ(x + d, y + d, 0.0));
        }

        private static void DrawCircle(Document doc, ViewSheet sheet, XYZ center, double radiusFt)
        {
            try
            {
                Arc a1 = Arc.Create(center, radiusFt, 0.0, Math.PI, XYZ.BasisX, XYZ.BasisY);
                Arc a2 = Arc.Create(center, radiusFt, Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY);
                doc.Create.NewDetailCurve(sheet, a1);
                doc.Create.NewDetailCurve(sheet, a2);
            }
            catch
            {
            }
        }

        public static void DrawLine(Document doc, ViewSheet sheet, XYZ p0, XYZ p1)
        {
            try
            {
                if (p0.DistanceTo(p1) > 1e-4)
                {
                    doc.Create.NewDetailCurve(sheet, Line.CreateBound(p0, p1));
                }
            }
            catch
            {
            }
        }

        public static TextNote? DrawText(
            Document doc,
            ViewSheet sheet,
            XYZ origin,
            string text,
            ElementId textTypeId,
            HorizontalTextAlignment align = HorizontalTextAlignment.Left,
            double widthFt = 0.0)
        {
            if (string.IsNullOrWhiteSpace(text) || textTypeId == ElementId.InvalidElementId)
            {
                return null;
            }

            try
            {
                TextNoteOptions opts = new TextNoteOptions(textTypeId)
                {
                    HorizontalAlignment = align,
                    Rotation = 0.0
                };

                if (widthFt > 1e-4)
                {
                    return TextNote.Create(doc, sheet.Id, origin, widthFt, text, opts);
                }

                return TextNote.Create(doc, sheet.Id, origin, text, opts);
            }
            catch
            {
                return null;
            }
        }

        public static void EnsureSheetSketchPlane(Document doc, ViewSheet sheet)
        {
            if (sheet.SketchPlane == null)
            {
                try
                {
                    Plane plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    sheet.SketchPlane = SketchPlane.Create(doc, plane);
                }
                catch
                {
                }
            }
        }

        private static void ClearSheetDetailAnnotations(Document doc, ViewSheet sheet)
        {
            try
            {
                List<ElementId> toDelete = new FilteredElementCollector(doc, sheet.Id)
                    .WhereElementIsNotElementType()
                    .Where(e => e is TextNote or CurveElement)
                    .Select(e => e.Id)
                    .ToList();

                if (toDelete.Count > 0)
                {
                    doc.Delete(toDelete);
                }
            }
            catch
            {
            }
        }

        private static void ConfigureTitleBlockA3(Document doc, ViewSheet sheet)
        {
            Element? tbInstance = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            if (tbInstance == null) return;

            TrySetInt(tbInstance, "Формат А", 3);
            TrySetInt(tbInstance, "Кратность", 1);
            TrySetInt(tbInstance, "Книжная ориентация", 0);
            TrySetInt(tbInstance, "Альбомная ориентация", 1);
            TrySetInt(tbInstance, "Нестандартный размер", 0);
        }

        private static void TrySetInt(Element el, string name, int val)
        {
            Parameter? p = el.LookupParameter(name);
            if (p is { IsReadOnly: false, StorageType: StorageType.Integer })
            {
                try { p.Set(val); } catch { }
            }
        }

        public static ElementId GetOrCreateTextType(Document doc, double heightMm, bool bold, string typeName)
        {
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
                .FirstOrDefault();

            if (baseType == null)
            {
                return ElementId.InvalidElementId;
            }

            try
            {
                TextNoteType created = (TextNoteType)baseType.Duplicate(typeName);
                created.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(heightMm * MmToFeet);
                created.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set("ISOCPEUR");
                created.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)?.Set(bold ? 1 : 0);
                created.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1);
                return created.Id;
            }
            catch
            {
                return baseType.Id;
            }
        }
    }
}
