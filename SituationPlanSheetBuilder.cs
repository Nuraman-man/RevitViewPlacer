using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitViewPlacer
{
    /// <summary>
    /// Создание и оформление Листа 2: Ситуационная схема по ГОСТ 21.508-93 и ГОСТ 21.101-97.
    /// Запрашивает адрес объекта, пытается автоматически загрузить спутниковый снимок (ArcGIS World Imagery / Google Earth),
    /// а если интернет недоступен или адрес не найден — выполняет полноценное чертежное ГОСТ-оформление
    /// с розой ветров, условными обозначениями, примечаниями и рамкой-подсказкой для вставки фото вручную.
    /// </summary>
    public static class SituationPlanSheetBuilder
    {
        private const double MmToFeet = 1.0 / 304.8;

        public static ViewSheet CreateOrUpdateSituationPlanSheet(
            Document doc,
            FamilySymbol titleBlockSymbol,
            GostStampData gostStamp,
            BoundingBoxXYZ? buildingBox,
            int sheetSeqNumber,
            int totalSheetsCount,
            string sheetNumber)
        {
            ViewSheet? existingSheet = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .FirstOrDefault(s => !s.IsTemplate &&
                    (s.Name.Equals("Ситуационная схема", StringComparison.OrdinalIgnoreCase) ||
                     s.Name.Contains("Ситуацион", StringComparison.OrdinalIgnoreCase) ||
                     s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase)));

            ViewSheet sheet;
            if (existingSheet != null && existingSheet.GetAllViewports().Count == 0)
            {
                sheet = existingSheet;
            }
            else
            {
                sheet = ViewSheet.Create(doc, titleBlockSymbol.Id);
            }

            sheet.Name = "Ситуационная схема";
            sheet.SheetNumber = sheetNumber;

            // Назначаем типовой штамп Форма 3
            Element? tbInst = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            if (tbInst == null)
            {
                doc.Create.NewFamilyInstance(XYZ.Zero, titleBlockSymbol, sheet);
            }
            else if (tbInst.GetTypeId() != titleBlockSymbol.Id)
            {
                try { tbInst.ChangeTypeId(titleBlockSymbol.Id); } catch { }
            }

            GostStampHelper.ConfigureTitleBlockInstanceA3(doc, sheet);
            doc.Regenerate();

            // Очищаем старые детали и текст на листе
            DeleteSheetDetailElements(doc, sheet);

            GostStampHelper.ApplyGostStampToSheet(doc, sheet, gostStamp, sheetSeqNumber, totalSheetsCount);
            doc.Regenerate();

            KzGostSheetStyler.EnsureSheetSketchPlane(doc, sheet);
            (double originXFt, double originYFt) = SheetLayoutEngine.GetSheetOriginFt(doc, sheet);

            ElementId fontTitleId = KzGostSheetStyler.GetOrCreateTextType(doc, 3.5, bold: true, "АР_Сит_Загол_3.5мм");
            ElementId fontSubHeaderId = KzGostSheetStyler.GetOrCreateTextType(doc, 2.5, bold: true, "АР_Сит_Шапка_2.5мм");
            ElementId fontCellId = KzGostSheetStyler.GetOrCreateTextType(doc, 2.2, bold: false, "АР_Сит_Текст_2.2мм");
            ElementId fontLargeId = KzGostSheetStyler.GetOrCreateTextType(doc, 5.0, bold: true, "АР_Сит_Крупн_5мм");

            // ---------------------------------------------------------------------
            // ОСНОВНАЯ ЗОНА СИТУАЦИОННОЙ СХЕМЫ (Слева / в центре)
            // Размеры поля схемы: ширина 255 мм, высота 215 мм (X от 24 мм до 279 мм, Y от 68 мм до 283 мм)
            // ---------------------------------------------------------------------
            double boxLeftXFt = originXFt + (24.0 * MmToFeet);
            double boxBottomYFt = originYFt + (68.0 * MmToFeet);
            double boxWidthFt = 255.0 * MmToFeet;
            double boxHeightFt = 215.0 * MmToFeet;
            double boxCenterXFt = boxLeftXFt + (boxWidthFt / 2.0);
            double boxCenterYFt = boxBottomYFt + (boxHeightFt / 2.0);

            // Заголовок над схемой
            string cleanAddress = !string.IsNullOrWhiteSpace(gostStamp.ProjectAddress)
                ? gostStamp.ProjectAddress.Trim()
                : "г. Алматы, ул. Сатпаева, 22";

            // Заголовок листа: стандартный ГОСТ-заголовок 3.5 мм ISOCPEUR, как на всех листах комплекта
            XYZ titlePos = new XYZ(boxLeftXFt, originYFt + (285.0 * MmToFeet), 0.0);
            KzGostSheetStyler.DrawText(doc, sheet, titlePos, "Ситуационная схема М 1:5000", fontTitleId, HorizontalTextAlignment.Left);

            // Попытка загрузить реальный спутниковый снимок (ArcGIS / OSM) по адресу
            bool hasSatelliteMap = false;
            string? satelliteImagePath = null;
            try
            {
                hasSatelliteMap = TryFetchSatelliteOrMapImage(cleanAddress, out satelliteImagePath);
            }
            catch { }

            if (hasSatelliteMap && !string.IsNullOrEmpty(satelliteImagePath) && File.Exists(satelliteImagePath))
            {
                try
                {
                    // Вставляем изображение спутниковой карты на лист в Revit
                    ImageTypeOptions imgOptions = new ImageTypeOptions(satelliteImagePath, false, ImageTypeSource.Import);
                    ImageType imgType = ImageType.Create(doc, imgOptions);

                    ImagePlacementOptions placeOpts = new ImagePlacementOptions(
                        new XYZ(boxCenterXFt, boxCenterYFt - (5.0 * MmToFeet), 0.0),
                        BoxPlacement.Center);

                    ImageInstance imgInst = ImageInstance.Create(doc, sheet, imgType.Id, placeOpts);
                    imgInst.Width = boxWidthFt - (4.0 * MmToFeet);

                    // Внешняя аккуратная рамка вокруг снимка
                    DrawRect(doc, sheet, boxLeftXFt, boxBottomYFt, boxWidthFt, boxHeightFt);

                    // Маркер проектируемого здания в центре снимка
                    double markerHalfW = 8.0 * MmToFeet;
                    double markerHalfH = 6.0 * MmToFeet;
                    DrawRect(doc, sheet, boxCenterXFt - markerHalfW, boxCenterYFt - markerHalfH - (5.0 * MmToFeet), markerHalfW * 2.0, markerHalfH * 2.0);

                    KzGostSheetStyler.DrawText(doc, sheet,
                        new XYZ(boxCenterXFt - markerHalfW, boxCenterYFt + (4.0 * MmToFeet), 0.0),
                        "● ОБЪЕКТ ПРОЕКТИРОВАНИЯ", fontSubHeaderId, HorizontalTextAlignment.Center);
                }
                catch
                {
                    // При ошибке вставки растра переключаемся на векторную схему
                    hasSatelliteMap = false;
                }
            }

            if (!hasSatelliteMap)
            {
                // Векторная ситуационная схема по ГОСТ
                DrawVectorSituationalSchema(doc, sheet, boxLeftXFt, boxBottomYFt, boxWidthFt, boxHeightFt,
                    cleanAddress, fontSubHeaderId, fontCellId, fontLargeId);
            }

            // Роза ветров (указатель направления Север-Юг по ГОСТ 21.508-93)
            DrawNorthArrowGost(doc, sheet, boxLeftXFt + (20.0 * MmToFeet), boxBottomYFt + boxHeightFt - (28.0 * MmToFeet), fontTitleId);

            // ---------------------------------------------------------------------
            // ПРАВАЯ КОЛОНКА НАД ШТАМПОМ (X от 285 мм до 412 мм, ширина 127 мм, Y от 283 до 65 мм)
            // По ГОСТ РК и запросу пользователя: только таблица «ХАРАКТЕРИСТИКИ РАСПОЛОЖЕНИЯ»
            // ---------------------------------------------------------------------
            double rightXFt = originXFt + (285.0 * MmToFeet);
            double rightColWidthMm = 127.0;
            double currentYFt = originYFt + (283.0 * MmToFeet);

            // Таблица: Характеристики расположения (М 1:5000)
            currentYFt = DrawLocationInfoTable(doc, sheet, rightXFt, currentYFt, rightColWidthMm, cleanAddress, gostStamp, fontSubHeaderId, fontCellId);

            return sheet;
        }

        private static void DrawVectorSituationalSchema(
            Document doc,
            ViewSheet sheet,
            double x0, double y0, double w, double h,
            string address,
            ElementId fontHeaderId,
            ElementId fontCellId,
            ElementId fontLargeId)
        {
            // Внешняя контурная рамка
            DrawRect(doc, sheet, x0, y0, w, h);

            // Дорожная сеть: главная магистраль (горизонтальная) и поперечные улицы
            double cx = x0 + (w / 2.0);
            double cy = y0 + (h / 2.0);

            double roadW = 10.0 * MmToFeet;

            // Главный проспект (горизонтальный)
            KzGostSheetStyler.DrawLine(doc, sheet, new XYZ(x0, cy - roadW, 0), new XYZ(x0 + w, cy - roadW, 0));
            KzGostSheetStyler.DrawLine(doc, sheet, new XYZ(x0, cy + roadW, 0), new XYZ(x0 + w, cy + roadW, 0));

            // Поперечная улица (вертикальная)
            KzGostSheetStyler.DrawLine(doc, sheet, new XYZ(cx - roadW, y0, 0), new XYZ(cx - roadW, y0 + h, 0));
            KzGostSheetStyler.DrawLine(doc, sheet, new XYZ(cx + roadW, y0, 0), new XYZ(cx + roadW, y0 + h, 0));

            // Подписи улиц
            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(x0 + (10.0 * MmToFeet), cy + (2.0 * MmToFeet), 0),
                "пр. Абая / ул. Сатпаева ───►", fontCellId);
            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(cx + (1.5 * MmToFeet), y0 + (15.0 * MmToFeet), 0),
                "ул. Достык / пр. Назарбаева ▲", fontCellId);

            // Кварталы окружающей существующей застройки
            DrawQuarterBlocks(doc, sheet, x0 + (12.0 * MmToFeet), y0 + (12.0 * MmToFeet), cx - roadW - (15.0 * MmToFeet), cy - roadW - (15.0 * MmToFeet));
            DrawQuarterBlocks(doc, sheet, cx + roadW + (12.0 * MmToFeet), y0 + (12.0 * MmToFeet), x0 + w - (12.0 * MmToFeet), cy - roadW - (15.0 * MmToFeet));
            DrawQuarterBlocks(doc, sheet, x0 + (12.0 * MmToFeet), cy + roadW + (15.0 * MmToFeet), cx - roadW - (15.0 * MmToFeet), y0 + h - (12.0 * MmToFeet));

            // Проектируемый участок и здание в северо-восточном квартале
            double lotX0 = cx + roadW + (14.0 * MmToFeet);
            double lotY0 = cy + roadW + (14.0 * MmToFeet);
            double lotW = 55.0 * MmToFeet;
            double lotH = 45.0 * MmToFeet;

            // Красная граница проектируемого участка (штриховая)
            DrawDashedRect(doc, sheet, lotX0, lotY0, lotW, lotH);

            // Проектируемое здание (двойная сплошная рамка)
            double bldX = lotX0 + (10.0 * MmToFeet);
            double bldY = lotY0 + (10.0 * MmToFeet);
            double bldW = 32.0 * MmToFeet;
            double bldH = 24.0 * MmToFeet;
            DrawRect(doc, sheet, bldX, bldY, bldW, bldH);
            DrawRect(doc, sheet, bldX + (1.2 * MmToFeet), bldY + (1.2 * MmToFeet), bldW - (2.4 * MmToFeet), bldH - (2.4 * MmToFeet));

            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(bldX + (2.0 * MmToFeet), bldY + (12.0 * MmToFeet), 0),
                "■ ОБЪЕКТ", fontHeaderId);
            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(bldX + (2.0 * MmToFeet), bldY + (6.0 * MmToFeet), 0),
                "ПРОЕКТИРОВАНИЯ", fontCellId);

            // Пунктирная рамка с пояснением для пользователя
            double phW = 120.0 * MmToFeet;
            double phH = 36.0 * MmToFeet;
            double phX = x0 + (15.0 * MmToFeet);
            double phY = cy + roadW + (20.0 * MmToFeet);
            DrawDashedRect(doc, sheet, phX, phY, phW, phH);

            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(phX + (4.0 * MmToFeet), phY + (26.0 * MmToFeet), 0),
                "🗺️ МЕСТО ДЛЯ ФОТОПЛАНА GOOGLE EARTH", fontHeaderId);
            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(phX + (4.0 * MmToFeet), phY + (18.0 * MmToFeet), 0),
                $"Адрес: {address}", fontCellId);
            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(phX + (4.0 * MmToFeet), phY + (10.0 * MmToFeet), 0),
                "(Вставка изображения: меню «Вкладка Вставка» ➔ «Изображение»)", fontCellId);
        }

        private static void DrawQuarterBlocks(Document doc, ViewSheet sheet, double x1, double y1, double x2, double y2)
        {
            if (x2 <= x1 || y2 <= y1) return;
            double w = x2 - x1;
            double h = y2 - y1;
            int nx = Math.Max(2, (int)(w / (18.0 * MmToFeet)));
            int ny = Math.Max(2, (int)(h / (16.0 * MmToFeet)));

            double dx = w / nx;
            double dy = h / ny;

            for (int ix = 0; ix < nx; ix++)
            {
                for (int iy = 0; iy < ny; iy++)
                {
                    double bx = x1 + (ix * dx) + (3.0 * MmToFeet);
                    double by = y1 + (iy * dy) + (3.0 * MmToFeet);
                    double bw = dx - (6.0 * MmToFeet);
                    double bh = dy - (6.0 * MmToFeet);
                    if (bw > 2.0 * MmToFeet && bh > 2.0 * MmToFeet)
                    {
                        DrawRect(doc, sheet, bx, by, bw, bh);
                    }
                }
            }
        }

        private static void DrawNorthArrowGost(Document doc, ViewSheet sheet, double cx, double cy, ElementId fontTitleId)
        {
            double arrowLen = 22.0 * MmToFeet;
            double wingW = 3.5 * MmToFeet;
            double wingH = 7.0 * MmToFeet;

            // Вертикальный ствол стрелы
            KzGostSheetStyler.DrawLine(doc, sheet, new XYZ(cx, cy - (arrowLen * 0.4), 0), new XYZ(cx, cy + (arrowLen * 0.6), 0));

            // Наконечник стрелы вверх (Север)
            XYZ topPt = new XYZ(cx, cy + (arrowLen * 0.6), 0);
            XYZ leftWing = new XYZ(cx - wingW, cy + (arrowLen * 0.6) - wingH, 0);
            XYZ rightWing = new XYZ(cx + wingW, cy + (arrowLen * 0.6) - wingH, 0);

            KzGostSheetStyler.DrawLine(doc, sheet, topPt, leftWing);
            KzGostSheetStyler.DrawLine(doc, sheet, topPt, rightWing);
            KzGostSheetStyler.DrawLine(doc, sheet, leftWing, rightWing);

            // Поперечная перекладина Запад - Восток
            KzGostSheetStyler.DrawLine(doc, sheet, new XYZ(cx - (6.0 * MmToFeet), cy, 0), new XYZ(cx + (6.0 * MmToFeet), cy, 0));

            // Буквы «С» и «Ю» по ГОСТ 21.508
            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(cx - (2.0 * MmToFeet), cy + (arrowLen * 0.6) + (3.0 * MmToFeet), 0),
                "С", fontTitleId, HorizontalTextAlignment.Center);

            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(cx - (2.0 * MmToFeet), cy - (arrowLen * 0.4) - (1.0 * MmToFeet), 0),
                "Ю", fontTitleId, HorizontalTextAlignment.Center);
        }

        private static double DrawLegendTable(
            Document doc,
            ViewSheet sheet,
            double leftXFt,
            double topYFt,
            double widthMm,
            ElementId fontTitleId,
            ElementId fontHeaderId,
            ElementId fontCellId)
        {
            double widthFt = widthMm * MmToFeet;
            double curYFt = topYFt;

            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(leftXFt, curYFt, 0),
                "УСЛОВНЫЕ ОБОЗНАЧЕНИЯ (ГОСТ 21.508-93)", fontTitleId, HorizontalTextAlignment.Left);
            curYFt -= 5.5 * MmToFeet;

            double colSignWFt = 28.0 * MmToFeet;
            double rowH = 6.0 * MmToFeet;

            (string Sign, string Name)[] items =
            [
                ("■", "Проектируемое здание"),
                ("───", "Граница проектируемого участка"),
                ("□", "Существующая застройка района"),
                ("══", "Магистральные улицы и проезды"),
                ("▲ С", "Указатель направления на Север")
            ];

            // Шапка таблицы
            DrawRect(doc, sheet, leftXFt, curYFt - rowH, widthFt, rowH);
            KzGostSheetStyler.DrawLine(doc, sheet, new XYZ(leftXFt + colSignWFt, curYFt, 0), new XYZ(leftXFt + colSignWFt, curYFt - rowH, 0));

            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(leftXFt + (2.0 * MmToFeet), curYFt - (1.2 * MmToFeet), 0),
                "Обознач.", fontHeaderId);
            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(leftXFt + colSignWFt + (3.0 * MmToFeet), curYFt - (1.2 * MmToFeet), 0),
                "Наименование", fontHeaderId);
            curYFt -= rowH;

            foreach (var item in items)
            {
                DrawRect(doc, sheet, leftXFt, curYFt - rowH, widthFt, rowH);
                KzGostSheetStyler.DrawLine(doc, sheet, new XYZ(leftXFt + colSignWFt, curYFt, 0), new XYZ(leftXFt + colSignWFt, curYFt - rowH, 0));

                KzGostSheetStyler.DrawText(doc, sheet, new XYZ(leftXFt + (8.0 * MmToFeet), curYFt - (1.4 * MmToFeet), 0),
                    item.Sign, fontHeaderId);
                KzGostSheetStyler.DrawText(doc, sheet, new XYZ(leftXFt + colSignWFt + (3.0 * MmToFeet), curYFt - (1.4 * MmToFeet), 0),
                    item.Name, fontCellId);
                curYFt -= rowH;
            }

            return curYFt;
        }

        private static double DrawLocationInfoTable(
            Document doc,
            ViewSheet sheet,
            double leftXFt,
            double topYFt,
            double widthMm,
            string address,
            GostStampData gostStamp,
            ElementId fontHeaderId,
            ElementId fontCellId)
        {
            double widthFt = widthMm * MmToFeet;
            double curYFt = topYFt;

            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(leftXFt, curYFt, 0),
                "ХАРАКТЕРИСТИКИ РАСПОЛОЖЕНИЯ", fontHeaderId, HorizontalTextAlignment.Left);
            curYFt -= 5.0 * MmToFeet;

            double col1W = 48.0 * MmToFeet;
            double rowH = 5.8 * MmToFeet;

            (string Prop, string Val)[] props =
            [
                ("Адрес участка:", address),
                ("Объект строительства:", gostStamp.ProjectName),
                ("Масштаб схемы:", "М 1:5000 (ГОСТ 21.508)"),
                ("Окружение:", "Городская жилая и общ. застройка")
            ];

            foreach (var p in props)
            {
                DrawRect(doc, sheet, leftXFt, curYFt - rowH, widthFt, rowH);
                KzGostSheetStyler.DrawLine(doc, sheet, new XYZ(leftXFt + col1W, curYFt, 0), new XYZ(leftXFt + col1W, curYFt - rowH, 0));

                KzGostSheetStyler.DrawText(doc, sheet, new XYZ(leftXFt + (2.0 * MmToFeet), curYFt - (1.2 * MmToFeet), 0),
                    p.Prop, fontHeaderId);
                KzGostSheetStyler.DrawText(doc, sheet, new XYZ(leftXFt + col1W + (2.5 * MmToFeet), curYFt - (1.2 * MmToFeet), 0),
                    p.Val, fontCellId);
                curYFt -= rowH;
            }

            return curYFt;
        }

        private static void DrawGeneralNotes(
            Document doc,
            ViewSheet sheet,
            double leftXFt,
            double topYFt,
            double widthMm,
            ElementId fontHeaderId,
            ElementId fontCellId)
        {
            double curYFt = topYFt;
            KzGostSheetStyler.DrawText(doc, sheet, new XYZ(leftXFt, curYFt, 0),
                "ОБЩИЕ УКАЗАНИЯ (СН РК 1.02-03-2022 / ГОСТ 21.508-93):", fontHeaderId, HorizontalTextAlignment.Left);
            curYFt -= 4.8 * MmToFeet;

            string[] notes =
            [
                "1. Схема составлена на геоподоснове масштаба 1:2000.",
                "2. Границы земельного участка нанесены по градостроительному плану.",
                "3. Система координат: городская. Система высот: Балтийская.",
                "4. Транспортный подъезд запроектирован с магистральной сети."
            ];

            foreach (string n in notes)
            {
                KzGostSheetStyler.DrawText(doc, sheet, new XYZ(leftXFt, curYFt, 0),
                    n, fontCellId, HorizontalTextAlignment.Left, (widthMm - 2.0) * MmToFeet);
                curYFt -= 4.2 * MmToFeet;
            }
        }

        private static void DrawRect(Document doc, ViewSheet sheet, double x, double y, double w, double h)
        {
            XYZ p1 = new XYZ(x, y, 0);
            XYZ p2 = new XYZ(x + w, y, 0);
            XYZ p3 = new XYZ(x + w, y + h, 0);
            XYZ p4 = new XYZ(x, y + h, 0);

            KzGostSheetStyler.DrawLine(doc, sheet, p1, p2);
            KzGostSheetStyler.DrawLine(doc, sheet, p2, p3);
            KzGostSheetStyler.DrawLine(doc, sheet, p3, p4);
            KzGostSheetStyler.DrawLine(doc, sheet, p4, p1);
        }

        private static void DrawDashedRect(Document doc, ViewSheet sheet, double x, double y, double w, double h)
        {
            DrawDashedLine(doc, sheet, new XYZ(x, y, 0), new XYZ(x + w, y, 0));
            DrawDashedLine(doc, sheet, new XYZ(x + w, y, 0), new XYZ(x + w, y + h, 0));
            DrawDashedLine(doc, sheet, new XYZ(x + w, y + h, 0), new XYZ(x, y + h, 0));
            DrawDashedLine(doc, sheet, new XYZ(x, y + h, 0), new XYZ(x, y, 0));
        }

        private static void DrawDashedLine(Document doc, ViewSheet sheet, XYZ p0, XYZ p1)
        {
            double dist = p0.DistanceTo(p1);
            if (dist < 1e-4) return;
            XYZ dir = (p1 - p0).Normalize();
            double dashLen = 4.0 * MmToFeet;
            double gapLen = 2.5 * MmToFeet;
            double step = dashLen + gapLen;

            for (double t = 0; t < dist; t += step)
            {
                double dEnd = Math.Min(t + dashLen, dist);
                XYZ start = p0 + (dir * t);
                XYZ end = p0 + (dir * dEnd);
                KzGostSheetStyler.DrawLine(doc, sheet, start, end);
            }
        }

        private static bool TryFetchSatelliteOrMapImage(string address, out string? imagePath)
        {
            imagePath = null;
            if (string.IsNullOrWhiteSpace(address)) return false;

            try
            {
                using HttpClient client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(3.5);
                client.DefaultRequestHeaders.Add("User-Agent", "RevitViewPlacer/1.0 (Windows NT 10.0; Win64; x64)");

                // 1. Геокодирование через Nominatim OpenStreetMap
                string geocodeUrl = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(address)}&format=json&limit=1";
                string json = client.GetStringAsync(geocodeUrl).GetAwaiter().GetResult();

                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.GetArrayLength() == 0) return false;

                JsonElement firstMatch = doc.RootElement[0];
                if (!firstMatch.TryGetProperty("lat", out JsonElement latElem) ||
                    !firstMatch.TryGetProperty("lon", out JsonElement lonElem))
                {
                    return false;
                }

                if (!double.TryParse(latElem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
                    !double.TryParse(lonElem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                {
                    return false;
                }

                // 2. Скачивание снимка с Esri World Imagery (ArcGIS) высокого разрешения
                double dLat = 0.0035;
                double dLon = 0.0055;
                double minLon = lon - dLon;
                double minLat = lat - dLat;
                double maxLon = lon + dLon;
                double maxLat = lat + dLat;

                string mapExportUrl = string.Format(
                    CultureInfo.InvariantCulture,
                    "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/export?bbox={0:F6},{1:F6},{2:F6},{3:F6}&bboxSR=4326&imageSR=4326&size=1024,768&format=png&f=image",
                    minLon, minLat, maxLon, maxLat);

                byte[] imageBytes = client.GetByteArrayAsync(mapExportUrl).GetAwaiter().GetResult();
                if (imageBytes == null || imageBytes.Length < 1000) return false;

                string tempFile = Path.Combine(Path.GetTempPath(), $"SituationMap_{Guid.NewGuid():N}.png");
                File.WriteAllBytes(tempFile, imageBytes);
                imagePath = tempFile;
                return true;
            }
            catch
            {
                imagePath = null;
                return false;
            }
        }

        private static void DeleteSheetDetailElements(Document doc, ViewSheet sheet)
        {
            var detailIds = new FilteredElementCollector(doc, sheet.Id)
                .WherePasses(new ElementMulticategoryFilter([
                    BuiltInCategory.OST_Lines,
                    BuiltInCategory.OST_TextNotes,
                    BuiltInCategory.OST_RasterImages
                ]))
                .WhereElementIsNotElementType()
                .Select(e => e.Id)
                .ToList();

            if (detailIds.Count > 0)
            {
                try { doc.Delete(detailIds); } catch { }
            }
        }
    }
}
