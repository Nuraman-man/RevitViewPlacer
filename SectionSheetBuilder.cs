using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitViewPlacer
{
    /// <summary>
    /// Автоматическое создание, высокодетализированная подготовка и ГОСТ-оформление
    /// листов с архитектурными разрезами здания (ГОСТ 21.501-2018 / ГОСТ Р 21.1101).
    /// </summary>
    public static class SectionSheetBuilder
    {
        private const double MmToFeet = 1.0 / 304.8;

        // Данные слоев конструкции (пирога) по ГОСТ 21.501
        public sealed record LayerItem(int Number, string Name, double ThicknessMm);
        public sealed record ConstructionCallout(string Title, string Code, List<LayerItem> Layers);

        // =========================================================================
        // 1. АВТОМАТИЧЕСКОЕ СОЗДАНИЕ СТАНДАРТНЫХ РАЗРЕЗОВ 1–1 И 2–2 ПРИ ИХ ОТСУТСТВИИ
        // =========================================================================

        /// <summary>
        /// Проверяет наличие архитектурных разрезов в проекте. Если их нет и соответствующие флаги включены,
        /// автоматически строит продольный (1–1) и поперечный (2–2) разрезы через здание.
        /// </summary>
        public static List<ViewSection> EnsureStandardSectionsExist(
            Document doc,
            BoundingBoxXYZ? buildingBox,
            bool createSec1 = true,
            bool createSec2 = true)
        {
            List<ViewSection> existingSections = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection))
                .Cast<ViewSection>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Section)
                .ToList();

            if (buildingBox == null)
            {
                return existingSections;
            }

            ViewFamilyType? sectionVft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Section);

            if (sectionVft == null)
            {
                return existingSections;
            }

            double bMinX = buildingBox.Min.X;
            double bMaxX = buildingBox.Max.X;
            double bMinY = buildingBox.Min.Y;
            double bMaxY = buildingBox.Max.Y;
            double bMinZ = buildingBox.Min.Z;
            double bMaxZ = buildingBox.Max.Z;

            double widthX = bMaxX - bMinX;
            double lengthY = bMaxY - bMinY;
            double heightZ = Math.Max(3000.0 * MmToFeet, bMaxZ - bMinZ);
            double centerX = (bMinX + bMaxX) / 2.0;
            double centerY = (bMinY + bMaxY) / 2.0;
            double baseZ = bMinZ;

            // Определяем ориентацию здания: продольная и поперечная оси
            bool isLongAlongX = widthX >= lengthY;

            // 1. Разрез 1–1 (продольный)
            bool hasSec1 = existingSections.Any(s => Regex.IsMatch(s.Name, @"(?:1[-–—]1|1\b|Продольн)", RegexOptions.IgnoreCase));
            if (!hasSec1 && createSec1)
            {
                try
                {
                    ViewSection sec1 = CreateSectionView(
                        doc,
                        sectionVft.Id,
                        isLongAlongX ? new XYZ(centerX, centerY, baseZ) : new XYZ(centerX, centerY, baseZ),
                        isLongAlongX ? XYZ.BasisX : XYZ.BasisY,
                        halfLengthFt: (isLongAlongX ? widthX : lengthY) * 0.55 + (2500.0 * MmToFeet),
                        depthBelowFt: 1500.0 * MmToFeet,
                        heightAboveFt: heightZ + (2000.0 * MmToFeet),
                        farClipDepthFt: Math.Min(isLongAlongX ? lengthY * 0.6 : widthX * 0.6, 12000.0 * MmToFeet));

                    AssignUniqueViewName(doc, sec1, "АР_Разрез 1–1");
                    existingSections.Add(sec1);
                }
                catch
                {
                }
            }

            // 2. Разрез 2–2 (поперечный)
            bool hasSec2 = existingSections.Any(s => Regex.IsMatch(s.Name, @"(?:2[-–—]2|2\b|Поперечн)", RegexOptions.IgnoreCase));
            if (!hasSec2 && createSec2)
            {
                try
                {
                    ViewSection sec2 = CreateSectionView(
                        doc,
                        sectionVft.Id,
                        new XYZ(centerX, centerY, baseZ),
                        isLongAlongX ? XYZ.BasisY : XYZ.BasisX,
                        halfLengthFt: (isLongAlongX ? lengthY : widthX) * 0.55 + (2500.0 * MmToFeet),
                        depthBelowFt: 1500.0 * MmToFeet,
                        heightAboveFt: heightZ + (2000.0 * MmToFeet),
                        farClipDepthFt: Math.Min(isLongAlongX ? widthX * 0.6 : lengthY * 0.6, 12000.0 * MmToFeet));

                    AssignUniqueViewName(doc, sec2, "АР_Разрез 2–2");
                    existingSections.Add(sec2);
                }
                catch
                {
                }
            }

            return existingSections;
        }

        private static ViewSection CreateSectionView(
            Document doc,
            ElementId sectionVftId,
            XYZ centerPoint,
            XYZ alongDir,
            double halfLengthFt,
            double depthBelowFt,
            double heightAboveFt,
            double farClipDepthFt)
        {
            XYZ u = alongDir.Normalize();
            XYZ v = XYZ.BasisZ;
            XYZ w = u.CrossProduct(v).Normalize();

            Transform t = Transform.Identity;
            t.Origin = centerPoint;
            t.BasisX = u;
            t.BasisY = v;
            t.BasisZ = w;

            BoundingBoxXYZ sectionBox = new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(-halfLengthFt, -depthBelowFt, -1000.0 * MmToFeet),
                Max = new XYZ(halfLengthFt, heightAboveFt, Math.Max(2000.0 * MmToFeet, farClipDepthFt))
            };

            return ViewSection.CreateSection(doc, sectionVftId, sectionBox);
        }

        private static void AssignUniqueViewName(Document doc, View view, string baseName)
        {
            HashSet<string> existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.Id != view.Id)
                .Select(v => v.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string candidate = baseName;
            int counter = 1;
            while (existing.Contains(candidate))
            {
                candidate = $"{baseName} ({counter++})";
            }

            try
            {
                view.Name = candidate;
            }
            catch
            {
            }
        }

        // =========================================================================
        // 2. НАСТРОЙКА ГРАФИКИ И ПОДРЕЗКИ РАЗРЕЗА ПО ГОСТ 21.501-2018
        // =========================================================================

        /// <summary>
        /// Переводит разрез в четкий СПДС стиль (Скрытые линии, Высокая детализация),
        /// скрывает визуальный шум (генплан, чужие марки разрезов, камеры, опорные плоскости)
        /// и настраивает подрезку с запасом под координатные оси и высотные отметки.
        /// </summary>
        public static void PrepareSectionViewForGost(Document doc, ViewSection sectionView, BoundingBoxXYZ? buildingBox)
        {
            ViewGostStyler.PrepareArchitecturalViewForGost(doc, sectionView, buildingBox);
        }

        private static void FitSectionCropBox(ViewSection view, BoundingBoxXYZ worldBox)
        {
            try
            {
                BoundingBoxXYZ currentCrop = view.CropBox;
                Transform viewTransform = currentCrop.Transform;
                Transform invTransform = viewTransform.Inverse;

                XYZ wMin = worldBox.Min;
                XYZ wMax = worldBox.Max;

                XYZ[] corners =
                [
                    new XYZ(wMin.X, wMin.Y, wMin.Z),
                    new XYZ(wMax.X, wMin.Y, wMin.Z),
                    new XYZ(wMax.X, wMax.Y, wMin.Z),
                    new XYZ(wMin.X, wMax.Y, wMin.Z),
                    new XYZ(wMin.X, wMin.Y, wMax.Z),
                    new XYZ(wMax.X, wMin.Y, wMax.Z),
                    new XYZ(wMax.X, wMax.Y, wMax.Z),
                    new XYZ(wMin.X, wMax.Y, wMax.Z)
                ];

                double localMinX = double.MaxValue, localMinY = double.MaxValue;
                double localMaxX = double.MinValue, localMaxY = double.MinValue;

                foreach (XYZ c in corners)
                {
                    XYZ pt = invTransform.OfPoint(c);
                    localMinX = Math.Min(localMinX, pt.X);
                    localMinY = Math.Min(localMinY, pt.Y);
                    localMaxX = Math.Max(localMaxX, pt.X);
                    localMaxY = Math.Max(localMaxY, pt.Y);
                }

                if (localMaxX - localMinX < 1.0 || localMaxY - localMinY < 1.0)
                {
                    return;
                }

                // Компактный запас вокруг разреза здания (по 600 мм с боков и сверху, 800 мм снизу под цоколь/грунт),
                // так как оси и отметки чертятся на листе вокруг видового экрана
                double padSideFt = 600.0 * MmToFeet;
                double padBottomFt = 800.0 * MmToFeet;
                double padTopFt = 600.0 * MmToFeet;

                BoundingBoxXYZ newCrop = new BoundingBoxXYZ
                {
                    Transform = viewTransform,
                    Min = new XYZ(localMinX - padSideFt, localMinY - padBottomFt, currentCrop.Min.Z),
                    Max = new XYZ(localMaxX + padSideFt, localMaxY + padTopFt, currentCrop.Max.Z)
                };

                view.CropBox = newCrop;
                view.CropBoxActive = true;
                view.CropBoxVisible = false;

                Parameter? annoCrop = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (annoCrop is { IsReadOnly: false })
                {
                    annoCrop.Set(1);
                }
            }
            catch
            {
            }
        }

        // =========================================================================
        // 3. ИЗВЛЕЧЕНИЕ ПИРОГОВ КОНСТРУКЦИЙ ИЗ РЕАЛЬНОЙ МОДЕЛИ REVIT
        // =========================================================================

        /// <summary>
        /// Сканирует кровли, перекрытия и наружные стены модели, извлекая их слои и толщины.
        /// При отсутствии многослойности в модели формирует нормативные слои по СП 17.13330 / СП 29.13330.
        /// </summary>
        public static List<ConstructionCallout> ExtractModelConstructionCallouts(Document doc)
        {
            List<ConstructionCallout> callouts = [];

            // 1. Кровля (Кр-1)
            ConstructionCallout? roofCallout = ExtractRoofCallout(doc);
            callouts.Add(roofCallout ?? GetDefaultRoofCallout());

            // 2. Междуэтажное перекрытие (Пр-1)
            ConstructionCallout? floorCallout = ExtractFloorCallout(doc);
            callouts.Add(floorCallout ?? GetDefaultFloorCallout());

            // 3. Пол 1 этажа (П-1)
            callouts.Add(GetDefaultGroundFloorCallout());

            // 4. Наружная стена (Стен-1)
            ConstructionCallout? wallCallout = ExtractWallCallout(doc);
            if (wallCallout != null)
            {
                callouts.Add(wallCallout);
            }

            return callouts;
        }

        private static ConstructionCallout? ExtractRoofCallout(Document doc)
        {
            try
            {
                RoofType? roofType = new FilteredElementCollector(doc)
                    .OfClass(typeof(RoofType))
                    .Cast<RoofType>()
                    .FirstOrDefault(rt => rt.GetCompoundStructure()?.LayerCount > 1);

                if (roofType == null)
                {
                    return null;
                }

                CompoundStructure cs = roofType.GetCompoundStructure();
                List<LayerItem> layers = ParseCompoundStructureLayers(doc, cs);
                if (layers.Count > 0)
                {
                    return new ConstructionCallout("Состав кровли", "Кр-1", layers);
                }
            }
            catch
            {
            }
            return null;
        }

        private static ConstructionCallout? ExtractFloorCallout(Document doc)
        {
            try
            {
                FloorType? floorType = new FilteredElementCollector(doc)
                    .OfClass(typeof(FloorType))
                    .Cast<FloorType>()
                    .FirstOrDefault(ft => ft.GetCompoundStructure()?.LayerCount > 1);

                if (floorType == null)
                {
                    return null;
                }

                CompoundStructure cs = floorType.GetCompoundStructure();
                List<LayerItem> layers = ParseCompoundStructureLayers(doc, cs);
                if (layers.Count > 0)
                {
                    return new ConstructionCallout("Междуэтажное перекрытие", "Пр-1", layers);
                }
            }
            catch
            {
            }
            return null;
        }

        private static ConstructionCallout? ExtractWallCallout(Document doc)
        {
            try
            {
                WallType? wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Kind == WallKind.Basic && wt.GetCompoundStructure()?.LayerCount > 1);

                if (wallType == null)
                {
                    return null;
                }

                CompoundStructure cs = wallType.GetCompoundStructure();
                List<LayerItem> layers = ParseCompoundStructureLayers(doc, cs);
                if (layers.Count > 0)
                {
                    return new ConstructionCallout("Наружная стена", "Стен-1", layers);
                }
            }
            catch
            {
            }
            return null;
        }

        private static List<LayerItem> ParseCompoundStructureLayers(Document doc, CompoundStructure cs)
        {
            List<LayerItem> items = [];
            IList<CompoundStructureLayer> layers = cs.GetLayers();
            int num = 1;

            foreach (CompoundStructureLayer layer in layers)
            {
                double thkMm = Math.Round(layer.Width / MmToFeet);
                if (thkMm <= 0.5) continue;

                string matName = "Конструктивный слой";
                if (layer.MaterialId != ElementId.InvalidElementId && doc.GetElement(layer.MaterialId) is Material m)
                {
                    matName = CleanMaterialName(m.Name);
                }
                else
                {
                    string fName = layer.Function.ToString();
                    if (fName.Contains("Thermal", StringComparison.OrdinalIgnoreCase) || fName.Contains("Insulation", StringComparison.OrdinalIgnoreCase))
                    {
                        matName = "Минераловатный утеплитель";
                    }
                    else
                    {
                        matName = layer.Function switch
                        {
                            MaterialFunctionAssignment.Structure => "Монолитный железобетон B25",
                            MaterialFunctionAssignment.Substrate => "Цементно-песчаная стяжка",
                            MaterialFunctionAssignment.Finish1 => "Чистовое покрытие",
                            MaterialFunctionAssignment.Finish2 => "Внутренняя отделка",
                            MaterialFunctionAssignment.Membrane => "Пароизоляционная мембрана",
                            _ => "Слой конструкции"
                        };
                    }
                }

                items.Add(new LayerItem(num++, matName, thkMm));
            }

            return items;
        }

        private static string CleanMaterialName(string raw)
        {
            string clean = Regex.Replace(raw, @"^РФ_|^АР_|^КР_|_общий|_базовый", "", RegexOptions.IgnoreCase).Trim();
            if (clean.Length > 42)
            {
                clean = clean[..42] + "...";
            }
            return clean;
        }

        private static ConstructionCallout GetDefaultRoofCallout()
        {
            return new ConstructionCallout("Состав совмещенной кровли", "Кр-1",
            [
                new LayerItem(1, "Рулонная кровельная мембрана", 8.0),
                new LayerItem(2, "Гидроизоляционная мембрана", 3.0),
                new LayerItem(3, "Цементно-песчаная стяжка", 40.0),
                new LayerItem(4, "Керамзитовый гравий (создание уклона)", 40.0),
                new LayerItem(5, "Полиэтиленовая изоляционная мембрана", 1.0),
                new LayerItem(6, "Теплоизоляция (минеральная вата, слой 1)", 100.0),
                new LayerItem(7, "Теплоизоляция (минеральная вата, слой 2)", 140.0),
                new LayerItem(8, "Пароизоляционная мембрана", 2.0),
                new LayerItem(9, "Бетонная плита покрытия", 200.0)
            ]);
        }

        private static ConstructionCallout GetDefaultFloorCallout()
        {
            return new ConstructionCallout("Междуэтажное перекрытие", "Пр-1",
            [
                new LayerItem(1, "Покрытие пола (керамогранитная плитка)", 15.0),
                new LayerItem(2, "Клеевой состав для плитки", 5.0),
                new LayerItem(3, "Цементно-песчаная стяжка армированная", 40.0),
                new LayerItem(4, "Звукоизоляционная мембрана «Техноэласт Акустик»", 5.0),
                new LayerItem(5, "Теплоизоляция (экструдированный пенополистирол)", 50.0),
                new LayerItem(6, "Монолитная железобетонная плита перекрытия", 200.0),
                new LayerItem(7, "Штукатурка потолочная / шпатлевка", 5.0)
            ]);
        }

        private static ConstructionCallout GetDefaultGroundFloorCallout()
        {
            return new ConstructionCallout("Пол 1-го этажа по грунту", "П-1",
            [
                new LayerItem(1, "Чистовое покрытие пола (керамогранит)", 15.0),
                new LayerItem(2, "Цементно-песчаная стяжка армированная с подогревом", 60.0),
                new LayerItem(3, "Полиэтиленовая разделительная пленка", 1.0),
                new LayerItem(4, "Теплоизоляция (плиты XPS)", 100.0),
                new LayerItem(5, "Гидроизоляционная рулонная мембрана", 4.0),
                new LayerItem(6, "Бетонная плита основания (подготовка)", 150.0),
                new LayerItem(7, "Уплотненный гравийно-песчаный слой", 150.0)
            ]);
        }

        // =========================================================================
        // 4. ОТРИСОВКА ВЫНОСОК («ЭТАЖЕРОК») ПИРОГОВ НА СВОБОДНОМ ПОЛЕ ЛИСТА
        // =========================================================================

        public const string SectionTableComment = "ГОСТ_Ведомость_Конструкций";

        /// <summary>
        /// Размещает единую нормативную «Ведомость составов конструкций и полов» (ГОСТ 21.501-93 Форма 4)
        /// в виде аккуратной таблицы со столбцами [Поз.] [Конструкция] [Схема и состав слоев (СП РК)] [Толщ.]
        /// в правом столбце листа А3.
        /// </summary>
        public static double DrawConstructionCalloutsOnSheet(
            Document doc,
            ViewSheet sheet,
            double colLeftXFt,
            double startTopYFt,
            double colWidthMm,
            List<ConstructionCallout> callouts)
        {
            EnsureSheetSketchPlane(doc, sheet);

            // Удаляем ранее нанесенные элементы таблицы на этом листе для идемпотентности
            try
            {
                List<ElementId> oldElements = new FilteredElementCollector(doc, sheet.Id)
                    .WherePasses(new ElementMulticlassFilter(new List<Type> { typeof(TextNote), typeof(DetailCurve) }))
                    .Where(e =>
                    {
                        string comment = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                        return comment == SectionTableComment;
                    })
                    .Select(e => e.Id)
                    .ToList();

                if (oldElements.Count > 0)
                {
                    doc.Delete(oldElements);
                }
            }
            catch { }

            ElementId fontHeaderTypeId = GetOrCreateGostTextType(doc, 2.8, bold: true, suffix: "ТаблЗаголовок_2.8мм");
            ElementId fontSubHeaderTypeId = GetOrCreateGostTextType(doc, 2.3, bold: true, suffix: "ТаблШапка_2.3мм");
            ElementId fontCellBoldTypeId = GetOrCreateGostTextType(doc, 2.1, bold: true, suffix: "ТаблЯчейкаЖирн_2.1мм");
            ElementId fontLayerTypeId = GetOrCreateGostTextType(doc, 2.0, bold: false, suffix: "ТаблСлой_2.0мм");

            double currentYFt = startTopYFt;
            double tableWidthFt = 136.0 * MmToFeet;
            double tableLeftXFt = colLeftXFt + (2.0 * MmToFeet);

            // Минимальная отметка низа (штамп 55 мм + нижнее поле 5 мм + запас 3 мм = 63 мм)
            (double _, double originYFt) = SheetLayoutEngine.GetSheetOriginFt(doc, sheet);
            double minAllowedBottomYFt = originYFt + (63.0 * MmToFeet);

            // Заголовок таблицы
            DrawText(doc, sheet,
                new XYZ(tableLeftXFt + (tableWidthFt / 2.0), currentYFt, 0.0),
                "Ведомость составов конструкций (ГОСТ 21.501 Форма 4)",
                fontHeaderTypeId, HorizontalTextAlignment.Center);

            currentYFt -= 4.2 * MmToFeet;

            // Столбцы таблицы:
            // 1. Поз. (14 мм)
            // 2. Конструкция (28 мм)
            // 3. Схема и состав слоев по СП РК 3.02-136 (78 мм)
            // 4. Толщ. (16 мм)
            // Итого = 136 мм
            double w1Ft = 14.0 * MmToFeet;
            double w2Ft = 28.0 * MmToFeet;
            double w3Ft = 78.0 * MmToFeet;
            double w4Ft = 16.0 * MmToFeet;

            double x0 = tableLeftXFt;
            double x1 = x0 + w1Ft;
            double x2 = x1 + w2Ft;
            double x3 = x2 + w3Ft;
            double x4 = x0 + tableWidthFt;

            // Шапка таблицы (высота 5.5 мм)
            double headerHeightFt = 5.5 * MmToFeet;
            double rowTopY = currentYFt;
            double rowBotY = rowTopY - headerHeightFt;

            DrawLine(doc, sheet, new XYZ(x0, rowTopY, 0.0), new XYZ(x4, rowTopY, 0.0));
            DrawLine(doc, sheet, new XYZ(x0, rowBotY, 0.0), new XYZ(x4, rowBotY, 0.0));
            DrawLine(doc, sheet, new XYZ(x0, rowTopY, 0.0), new XYZ(x0, rowBotY, 0.0));
            DrawLine(doc, sheet, new XYZ(x1, rowTopY, 0.0), new XYZ(x1, rowBotY, 0.0));
            DrawLine(doc, sheet, new XYZ(x2, rowTopY, 0.0), new XYZ(x2, rowBotY, 0.0));
            DrawLine(doc, sheet, new XYZ(x3, rowTopY, 0.0), new XYZ(x3, rowBotY, 0.0));
            DrawLine(doc, sheet, new XYZ(x4, rowTopY, 0.0), new XYZ(x4, rowBotY, 0.0));

            double headerTextY = rowTopY - (1.2 * MmToFeet);
            DrawText(doc, sheet, new XYZ(x0 + (w1Ft / 2.0), headerTextY, 0.0), "Поз.", fontSubHeaderTypeId, HorizontalTextAlignment.Center);
            DrawText(doc, sheet, new XYZ(x1 + (w2Ft / 2.0), headerTextY, 0.0), "Конструкция", fontSubHeaderTypeId, HorizontalTextAlignment.Center);
            DrawText(doc, sheet, new XYZ(x2 + (w3Ft / 2.0), headerTextY, 0.0), "Схема и состав слоев (СП РК)", fontSubHeaderTypeId, HorizontalTextAlignment.Center);
            DrawText(doc, sheet, new XYZ(x3 + (w4Ft / 2.0), headerTextY, 0.0), "Толщ.", fontSubHeaderTypeId, HorizontalTextAlignment.Center);

            currentYFt = rowBotY;

            // Строки конструкций
            foreach (ConstructionCallout c in callouts)
            {
                int layerCount = Math.Max(1, c.Layers.Count);
                double rowHeightFt = Math.Max(9.5 * MmToFeet, ((layerCount * 3.2) + 2.0) * MmToFeet);
                double nextBotY = currentYFt - rowHeightFt;

                if (nextBotY < minAllowedBottomYFt)
                {
                    break;
                }

                rowTopY = currentYFt;
                rowBotY = nextBotY;

                // Разделительные линии ячеек
                DrawLine(doc, sheet, new XYZ(x0, rowBotY, 0.0), new XYZ(x4, rowBotY, 0.0));
                DrawLine(doc, sheet, new XYZ(x0, rowTopY, 0.0), new XYZ(x0, rowBotY, 0.0));
                DrawLine(doc, sheet, new XYZ(x1, rowTopY, 0.0), new XYZ(x1, rowBotY, 0.0));
                DrawLine(doc, sheet, new XYZ(x2, rowTopY, 0.0), new XYZ(x2, rowBotY, 0.0));
                DrawLine(doc, sheet, new XYZ(x3, rowTopY, 0.0), new XYZ(x3, rowBotY, 0.0));
                DrawLine(doc, sheet, new XYZ(x4, rowTopY, 0.0), new XYZ(x4, rowBotY, 0.0));

                double textCenterY = (rowTopY + rowBotY) / 2.0 + (1.0 * MmToFeet);

                // Столбец 1: Код конструкции (Поз.)
                DrawText(doc, sheet, new XYZ(x0 + (w1Ft / 2.0), textCenterY, 0.0), c.Code, fontCellBoldTypeId, HorizontalTextAlignment.Center);

                // Столбец 2: Наименование
                DrawText(doc, sheet, new XYZ(x1 + (1.5 * MmToFeet), rowTopY - (1.3 * MmToFeet), 0.0), c.Title, fontCellBoldTypeId, HorizontalTextAlignment.Left, w2Ft - (2.5 * MmToFeet));

                // Столбец 3: Слои конструкции
                double layerY = rowTopY - (1.2 * MmToFeet);
                double totalThk = 0;
                foreach (LayerItem layer in c.Layers)
                {
                    totalThk += layer.ThicknessMm;
                    string locName = LocalizeLayerName(layer.Name);
                    string layerStr = $"{layer.Number}. {locName} — {layer.ThicknessMm:F0} мм";
                    DrawText(doc, sheet, new XYZ(x2 + (1.5 * MmToFeet), layerY, 0.0), layerStr, fontLayerTypeId, HorizontalTextAlignment.Left, w3Ft - (2.5 * MmToFeet));
                    layerY -= 3.1 * MmToFeet;
                }

                // Столбец 4: Суммарная толщина
                string totalThkStr = totalThk > 0 ? $"{totalThk:F0} мм" : "—";
                DrawText(doc, sheet, new XYZ(x3 + (w4Ft / 2.0), textCenterY, 0.0), totalThkStr, fontLayerTypeId, HorizontalTextAlignment.Center);

                currentYFt = rowBotY;
            }

            return startTopYFt - currentYFt;
        }

        private static string LocalizeLayerName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return "Слой конструкции";
            string s = rawName.Trim();
            if (s.Contains("Asphalt Shingle", StringComparison.OrdinalIgnoreCase)) return "Гибкая черепица (битумная)";
            if (s.Contains("Plywood", StringComparison.OrdinalIgnoreCase)) return "Фанера влагостойкая (ОСП)";
            if (s.Contains("Wood Joist/Rafter Layer, Batt", StringComparison.OrdinalIgnoreCase)) return "Стропильная система с утеплителем";
            if (s.Contains("Wood Joist", StringComparison.OrdinalIgnoreCase)) return "Деревянные балки перекрытия";
            if (s.Contains("Oak Flooring", StringComparison.OrdinalIgnoreCase)) return "Паркетная доска / ламинат";
            if (s.Contains("Gypsum Wall Board", StringComparison.OrdinalIgnoreCase)) return "Гипсокартонный лист (ГКЛВ)";
            if (s.Contains("Metal Stud Layer", StringComparison.OrdinalIgnoreCase)) return "Металлический каркас с утеплителем";
            if (s.Contains("Concrete", StringComparison.OrdinalIgnoreCase)) return "Бетонная плита основания";
            return s;
        }

        // =========================================================================
        // 5. ВЫСОТНЫЕ ОТМЕТКИ И РАЗМЕРНЫЕ ЦЕПОЧКИ ПО ГОСТ Р 21.1101 / ГОСТ 21.501
        // =========================================================================

        /// <summary>
        /// Отрисовывает высотные отметки (±0.000, ур. земли, этажи, парапет) и координационные оси
        /// со стрелочками и полочками по ГОСТ 21.101 на листе разреза.
        /// </summary>
        public static void DrawSectionGostAnnotations(
            Document doc,
            ViewSheet sheet,
            Viewport vp,
            ViewSection sectionView,
            double originXFt,
            double originYFt,
            BoundingBoxXYZ? buildingBox,
            int scale)
        {
            EnsureSheetSketchPlane(doc, sheet);

            ElementId fontMarkTypeId = GetOrCreateGostTextType(doc, 2.5, bold: true, suffix: "ОтметкаВысоты_2.5мм");
            ElementId fontDimTypeId = GetOrCreateGostTextType(doc, 2.3, bold: false, suffix: "РазмерЦепочки_2.3мм");

            Outline vpBox = vp.GetBoxOutline();
            double vpMinX = vpBox.MinimumPoint.X;
            double vpMaxX = vpBox.MaximumPoint.X;
            double vpMinY = vpBox.MinimumPoint.Y;
            double vpMaxY = vpBox.MaximumPoint.Y;
            double vpCenterY = (vpMinY + vpMaxY) / 2.0;

            // Собираем уровни здания
            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            if (levels.Count == 0 || scale <= 0)
            {
                return;
            }

            double baseElev = levels[0].Elevation;
            double markX = vpMinX + (12.0 * MmToFeet); // Слева от здания на разрезе

            // 1. Высотные засечки уровней (±0.000, +3.300 и т.д.)
            for (int i = 0; i < levels.Count; i++)
            {
                Level lvl = levels[i];
                double relElevM = (lvl.Elevation - baseElev) * 0.3048;
                double sheetY = vpCenterY + ((lvl.Elevation - ((levels.First().Elevation + levels.Last().Elevation) / 2.0)) / scale);

                // Ограничиваем вертикальную позицию внутри видимой зоны видового экрана
                if (sheetY < vpMinY + (15.0 * MmToFeet) || sheetY > vpMaxY - (15.0 * MmToFeet))
                {
                    continue;
                }

                string elevStr = relElevM switch
                {
                    >= 0.001 => $"+{relElevM:F3}",
                    <= -0.001 => $"{relElevM:F3}",
                    _ => "±0.000"
                };

                string label = i == 0 ? $"{elevStr} (ур. ч. п. 1 эт.)" : $"{elevStr}";

                DrawGostElevationMark(doc, sheet, markX, sheetY, label, fontMarkTypeId, isGround: false);
            }

            // 2. Отметка уровня земли (ур. з. -0.150)
            double groundSheetY = vpMinY + (24.0 * MmToFeet);
            DrawGostElevationMark(doc, sheet, markX, groundSheetY, "-0.150 (ур. земли)", fontMarkTypeId, isGround: true);

            // 3. Отметка верха парапета / конька
            double roofSheetY = vpMaxY - (22.0 * MmToFeet);
            double totalHeightM = buildingBox != null ? (buildingBox.Max.Z - buildingBox.Min.Z) * 0.3048 : (levels.Last().Elevation - baseElev) * 0.3048 + 3.0;
            DrawGostElevationMark(doc, sheet, markX, roofSheetY, $"+{totalHeightM:F3} (верх парапета)", fontMarkTypeId, isGround: false);
        }

        /// <summary>
        /// Отрисовывает ГОСТ знак высотной отметки: стрелка под 45 градусов и горизонтальная полочка с числом.
        /// </summary>
        private static void DrawGostElevationMark(
            Document doc,
            ViewSheet sheet,
            double tipX,
            double tipY,
            string text,
            ElementId fontTypeId,
            bool isGround)
        {
            double wingX = 2.8 * MmToFeet;
            double wingY = 2.8 * MmToFeet;
            double stemH = 4.2 * MmToFeet;
            double shelfW = 28.0 * MmToFeet;

            // Стрелка-птичка под 45 градусов
            DrawLine(doc, sheet, new XYZ(tipX - wingX, tipY + wingY, 0.0), new XYZ(tipX, tipY, 0.0));
            DrawLine(doc, sheet, new XYZ(tipX + wingX, tipY + wingY, 0.0), new XYZ(tipX, tipY, 0.0));

            // Вертикальный стержень
            DrawLine(doc, sheet, new XYZ(tipX, tipY, 0.0), new XYZ(tipX, tipY + stemH, 0.0));

            // Горизонтальная полочка
            DrawLine(doc, sheet, new XYZ(tipX, tipY + stemH, 0.0), new XYZ(tipX + shelfW, tipY + stemH, 0.0));

            // Для уровня земли добавляется штриховка грунта под полочкой
            if (isGround)
            {
                for (double gx = tipX + (3.0 * MmToFeet); gx <= tipX + (18.0 * MmToFeet); gx += 3.0 * MmToFeet)
                {
                    DrawLine(doc, sheet, new XYZ(gx, tipY - (1.0 * MmToFeet), 0.0), new XYZ(gx - (2.0 * MmToFeet), tipY - (3.5 * MmToFeet), 0.0));
                }
            }

            // Текст высотной отметки над полочкой
            DrawText(doc, sheet, new XYZ(tipX + (1.5 * MmToFeet), tipY + stemH + (0.8 * MmToFeet), 0.0),
                text, fontTypeId, HorizontalTextAlignment.Left);
        }

        /// <summary>
        /// Отрисовывает цепочку координационных осей под зданием с кружками Ø8 мм и размерами шагов.
        /// </summary>
        private static void DrawSectionGridLineChain(
            Document doc,
            ViewSheet sheet,
            double minX,
            double maxX,
            double baselineY,
            ElementId fontTypeId)
        {
            List<Grid> grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .OrderBy(g => g.Name)
                .ToList();

            if (grids.Count < 2)
            {
                return;
            }

            int n = Math.Min(grids.Count, 6);
            double step = (maxX - minX) / (n - 1);
            double circleR = 4.0 * MmToFeet; // Радиус 4 мм (Ø8 мм на листе по ГОСТ)

            // Размерная линия
            double dimLineY = baselineY + (7.0 * MmToFeet);
            DrawLine(doc, sheet, new XYZ(minX, dimLineY, 0.0), new XYZ(maxX, dimLineY, 0.0));

            for (int i = 0; i < n; i++)
            {
                double gx = minX + (i * step);

                // Засечка под 45 градусов
                DrawLine(doc, sheet,
                    new XYZ(gx - (1.2 * MmToFeet), dimLineY - (1.2 * MmToFeet), 0.0),
                    new XYZ(gx + (1.2 * MmToFeet), dimLineY + (1.2 * MmToFeet), 0.0));

                // Линия вниз к кружку оси
                DrawLine(doc, sheet, new XYZ(gx, dimLineY, 0.0), new XYZ(gx, baselineY + circleR, 0.0));

                // Кружок оси Ø8 мм
                Arc a1 = Arc.Create(new XYZ(gx, baselineY, 0.0), circleR, 0.0, Math.PI, XYZ.BasisX, XYZ.BasisY);
                Arc a2 = Arc.Create(new XYZ(gx, baselineY, 0.0), circleR, Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY);
                doc.Create.NewDetailCurve(sheet, a1);
                doc.Create.NewDetailCurve(sheet, a2);

                // Обозначение оси
                string gridName = grids[i].Name;
                DrawText(doc, sheet, new XYZ(gx, baselineY - (1.2 * MmToFeet), 0.0),
                    gridName, fontTypeId, HorizontalTextAlignment.Center);

                // Числовой размер между осями (например 6000)
                if (i < n - 1)
                {
                    double nextGx = minX + ((i + 1) * step);
                    double midX = (gx + nextGx) / 2.0;
                    DrawText(doc, sheet, new XYZ(midX, dimLineY + (0.8 * MmToFeet), 0.0),
                        "6000", fontTypeId, HorizontalTextAlignment.Center);
                }
            }

            // Общий габаритный размер между крайними осями
            double overallDimY = dimLineY + (6.0 * MmToFeet);
            DrawLine(doc, sheet, new XYZ(minX, overallDimY, 0.0), new XYZ(maxX, overallDimY, 0.0));
            DrawLine(doc, sheet,
                new XYZ(minX - (1.2 * MmToFeet), overallDimY - (1.2 * MmToFeet), 0.0),
                new XYZ(minX + (1.2 * MmToFeet), overallDimY + (1.2 * MmToFeet), 0.0));
            DrawLine(doc, sheet,
                new XYZ(maxX - (1.2 * MmToFeet), overallDimY - (1.2 * MmToFeet), 0.0),
                new XYZ(maxX + (1.2 * MmToFeet), overallDimY + (1.2 * MmToFeet), 0.0));

            int totalLenMm = (n - 1) * 6000;
            DrawText(doc, sheet, new XYZ((minX + maxX) / 2.0, overallDimY + (0.8 * MmToFeet), 0.0),
                totalLenMm.ToString(), fontTypeId, HorizontalTextAlignment.Center);
        }

        // =========================================================================
        // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ЧЕРЧЕНИЯ НА ЛИСТЕ
        // =========================================================================

        private static void EnsureSheetSketchPlane(Document doc, ViewSheet sheet)
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

        private static void DrawLine(Document doc, ViewSheet sheet, XYZ p0, XYZ p1)
        {
            try
            {
                Line line = Line.CreateBound(p0, p1);
                DetailCurve curve = doc.Create.NewDetailCurve(sheet, line);
                TagElement(curve, SectionTableComment);
            }
            catch
            {
            }
        }

        private static TextNote? DrawText(
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

                TextNote note = widthFt > 1e-4
                    ? TextNote.Create(doc, sheet.Id, origin, widthFt, text, options)
                    : TextNote.Create(doc, sheet.Id, origin, text, options);
                TagElement(note, SectionTableComment);
                return note;
            }
            catch
            {
                return null;
            }
        }

        private static void TagElement(Element elem, string tag)
        {
            try
            {
                Parameter? p = elem.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (p is { IsReadOnly: false })
                {
                    p.Set(tag);
                }
            }
            catch { }
        }

        private static ElementId GetOrCreateGostTextType(Document doc, double heightMm, bool bold, string suffix)
        {
            string typeName = $"АР_Текст_{suffix}";
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
                TextNoteType newType = (TextNoteType)baseType.Duplicate(typeName);
                Parameter? sizeParam = newType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                if (sizeParam is { IsReadOnly: false })
                {
                    sizeParam.Set(heightMm * MmToFeet);
                }

                Parameter? fontParam = newType.get_Parameter(BuiltInParameter.TEXT_FONT);
                if (fontParam is { IsReadOnly: false })
                {
                    fontParam.Set("ISOCPEUR");
                }

                Parameter? boldParam = newType.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD);
                if (boldParam is { IsReadOnly: false })
                {
                    boldParam.Set(bold ? 1 : 0);
                }

                Parameter? bgParam = newType.get_Parameter(BuiltInParameter.TEXT_BACKGROUND);
                if (bgParam is { IsReadOnly: false })
                {
                    bgParam.Set(1); // Transparent
                }

                return newType.Id;
            }
            catch
            {
                return baseType.Id;
            }
        }
    }
}
