using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitViewPlacer
{
    /// <summary>
    /// Движок точной подрезки видов (CropBox), расчета стандартного масштаба ГОСТ 2.302-68
    /// и математического центрирования видовых экранов (Viewport) и таблиц на листах А3 (Форма 3).
    /// </summary>
    public static class SheetLayoutEngine
    {
        private const double MmToFeet = 1.0 / 304.8;

        // Габариты листа А3 альбомной ориентации (420 х 297 мм) и рамки по ГОСТ Р 21.1101
        public const double SheetWidthMm = 420.0;
        public const double SheetHeightMm = 297.0;
        public const double MarginLeftMm = 20.0;       // Поле для подшивки слева (20 мм)
        public const double MarginRightMm = 5.0;       // Правая рамка (5 мм)
        public const double MarginTopMm = 5.0;         // Верхняя рамка (5 мм)
        public const double MarginBottomMm = 5.0;      // Нижняя рамка (5 мм)
        public const double StampWidthMm = 185.0;      // Ширина основной надписи Форма 3 (185 мм)
        public const double StampHeightMm = 55.0;      // Высота основной надписи Форма 3 (55 мм)
        private const double InnerPaddingMm = 6.0;     // Безопасный отступ внутри рабочей зоны

        // Строгий ряд стандартных масштабов уменьшения по ГОСТ 2.302-68 и ГОСТ 21.501-93 (Таблица 1):
        // Масштабы 1:150 и 1:250 исключены, так как они отсутствуют в ГОСТ 2.302-68!
        private static readonly int[] GostStandardScales =
        [
            1, 2, 5, 10, 20, 25, 40, 50, 75, 100, 200, 400, 500, 1000, 2000, 5000
        ];

        // Нормативные ряды масштабов по ГОСТ 21.501-93 (Таблица 1) и ГОСТ 21.508-93 для каждого типа чертежа
        private static readonly int[] GostSitePlanScales = [200, 500, 1000, 2000];
        private static readonly int[] GostFloorPlanScales = [50, 100, 200, 400];
        private static readonly int[] GostRoofAndCeilingPlanScales = [100, 200, 400, 500];
        private static readonly int[] GostFacadeScales = [40, 50, 75, 100, 200, 400];
        private static readonly int[] GostSectionScales = [20, 25, 40, 50, 75, 100, 200];
        private static readonly int[] GostDetailScales = [5, 10, 20, 25, 50];

        /// <summary>
        /// Вычисляет габаритный параллелепипед (BoundingBoxXYZ) самого здания в мировых координатах,
        /// исключая элементы генерального плана (OST_Site, OST_Planting и плиты "ГП_").
        /// </summary>
        public static BoundingBoxXYZ? ComputeBuildingWorldBoundingBox(Document doc)
        {
            BuiltInCategory[] buildingCategories =
            [
                BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_Roofs,
                BuiltInCategory.OST_Windows,
                BuiltInCategory.OST_Doors,
                BuiltInCategory.OST_Stairs,
                BuiltInCategory.OST_StairsRailing,
                BuiltInCategory.OST_CurtainWallPanels,
                BuiltInCategory.OST_CurtainWallMullions
            ];

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool foundAny = false;

            foreach (BuiltInCategory bic in buildingCategories)
            {
                List<Element> elems = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .ToList();

                foreach (Element el in elems)
                {
                    if (IsSitePlanElement(doc, el))
                    {
                        continue;
                    }

                    BoundingBoxXYZ? bb = el.get_BoundingBox(null);
                    if (bb == null)
                    {
                        continue;
                    }

                    minX = Math.Min(minX, bb.Min.X);
                    minY = Math.Min(minY, bb.Min.Y);
                    minZ = Math.Min(minZ, bb.Min.Z);
                    maxX = Math.Max(maxX, bb.Max.X);
                    maxY = Math.Max(maxY, bb.Max.Y);
                    maxZ = Math.Max(maxZ, bb.Max.Z);
                    foundAny = true;
                }
            }

            // Перекрытия добавляем только в том случае, если это не плиты генплана ("ГП_")
            List<Floor> buildingFloors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Floors)
                .WhereElementIsNotElementType()
                .OfType<Floor>()
                .Where(f => !IsSitePlanElement(doc, f))
                .ToList();

            foreach (Floor floor in buildingFloors)
            {
                BoundingBoxXYZ? bb = floor.get_BoundingBox(null);
                if (bb == null)
                {
                    continue;
                }

                minX = Math.Min(minX, bb.Min.X);
                minY = Math.Min(minY, bb.Min.Y);
                minZ = Math.Min(minZ, bb.Min.Z);
                maxX = Math.Max(maxX, bb.Max.X);
                maxY = Math.Max(maxY, bb.Max.Y);
                maxZ = Math.Max(maxZ, bb.Max.Z);
                foundAny = true;
            }

            if (!foundAny || maxX <= minX || maxY <= minY)
            {
                return null;
            }

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, Math.Max(maxZ, minZ + (3000.0 * MmToFeet)))
            };
        }

        public static bool IsSitePlanElement(Document doc, Element el)
        {
            if (el == null)
            {
                return false;
            }

            if (el is DirectShape)
            {
                return true;
            }

            if (el.Category != null)
            {
                ElementId cid = el.Category.Id;
                if (cid == new ElementId(BuiltInCategory.OST_Site)
                    || cid == new ElementId(BuiltInCategory.OST_Planting)
                    || cid == new ElementId(BuiltInCategory.OST_Toposolid)
                    || cid == new ElementId(BuiltInCategory.OST_Topography)
                    || cid == new ElementId(BuiltInCategory.OST_Roads)
                    || cid == new ElementId(BuiltInCategory.OST_Parking))
                {
                    return true;
                }
            }

            string mark = el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? string.Empty;
            string comments = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
            string elemName = el.Name ?? string.Empty;
            string typeName = string.Empty;
            try
            {
                ElementId typeId = el.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    typeName = doc.GetElement(typeId)?.Name ?? string.Empty;
                }
            }
            catch { }

            return mark.StartsWith("ГП", StringComparison.OrdinalIgnoreCase)
                   || mark.Contains("Генплан", StringComparison.OrdinalIgnoreCase)
                   || mark.Contains("Site", StringComparison.OrdinalIgnoreCase)
                   || comments.StartsWith("ГП", StringComparison.OrdinalIgnoreCase)
                   || comments.Contains("Генплан", StringComparison.OrdinalIgnoreCase)
                   || comments.Contains("Site", StringComparison.OrdinalIgnoreCase)
                   || elemName.StartsWith("ГП", StringComparison.OrdinalIgnoreCase)
                   || elemName.Contains("Генплан", StringComparison.OrdinalIgnoreCase)
                   || elemName.Contains("Site", StringComparison.OrdinalIgnoreCase)
                   || typeName.StartsWith("ГП", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("Генплан", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("Site", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Подготавливает архитектурный вид (АР_) к размещению на листе:
        /// 1) Скрывает элементы 3D-генплана (чтобы участок 50х50 м не уменьшал масштаб планов/фасадов/3D здания).
        /// 2) Скрывает далекие маркеры фасадов и гигантские неиспользуемые аннотации.
        /// 3) Аккуратно подрезает область подрезки (CropBox) строго по габаритам здания с полями под размеры.
        /// </summary>
        public static void PrepareArchitecturalViewForSheet(
            Document doc,
            View view,
            BoundingBoxXYZ? buildingWorldBBox)
        {
            bool isSiteView = view.Name.StartsWith("ГП_", StringComparison.OrdinalIgnoreCase)
                              || view.Name.Contains("Генплан", StringComparison.OrdinalIgnoreCase)
                              || view.Name.Contains("Генеральный план", StringComparison.OrdinalIgnoreCase);

            if (isSiteView)
            {
                return;
            }

            // Комплексная подготовка и оформление архитектурного вида по ГОСТ 21.501-93 / ГОСТ 21.101-97:
            // чистая графика HLR/Fine, скрытие шума, расстановка высотных отметок и линии земли ВНУТРИ вида, настройка CropBox
            ViewGostStyler.PrepareArchitecturalViewForGost(doc, view, buildingWorldBBox);
        }

        private static void TryHideCategoryInView(View view, ElementId categoryId)
        {
            try
            {
                if (view.CanCategoryBeHidden(categoryId))
                {
                    view.SetCategoryHidden(categoryId, true);
                }
            }
            catch
            {
                // Игнорируем категории, недоступные на данном типе вида
            }
        }

        /// <summary>
        /// Проецирует 8 вершин мирового BoundingBox здания в локальную систему координат вида
        /// и задает аккуратную область подрезки (CropBox) с отступом под размерные цепочки и отметки.
        /// </summary>
        public static void FitViewCropBoxToWorldBox(View view, BoundingBoxXYZ worldBox, double marginMm = 2000.0)
        {
            try
            {
                // Для 3D-перспективных видов (IsPerspective) подрезка работает иначе — пропускаем
                if (view is View3D { IsPerspective: true })
                {
                    return;
                }

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

                foreach (XYZ corner in corners)
                {
                    XYZ localPt = invTransform.OfPoint(corner);
                    localMinX = Math.Min(localMinX, localPt.X);
                    localMinY = Math.Min(localMinY, localPt.Y);
                    localMaxX = Math.Max(localMaxX, localPt.X);
                    localMaxY = Math.Max(localMaxY, localPt.Y);
                }

                if (localMaxX - localMinX < 1.0 || localMaxY - localMinY < 1.0)
                {
                    return;
                }

                double padFt = marginMm * MmToFeet;

                BoundingBoxXYZ newCrop = new BoundingBoxXYZ
                {
                    Transform = viewTransform,
                    Min = new XYZ(localMinX - padFt, localMinY - padFt, currentCrop.Min.Z),
                    Max = new XYZ(localMaxX + padFt, localMaxY + padFt, currentCrop.Max.Z)
                };

                view.CropBox = newCrop;
                view.CropBoxActive = true;
                view.CropBoxVisible = false;

                // Включаем подрезку аннотаций, чтобы далекие марки не смещали центр видового экрана
                Parameter? annoCropParam = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (annoCropParam is { IsReadOnly: false })
                {
                    annoCropParam.Set(1);
                }
            }
            catch
            {
                // Для специфических видов (легенд/чертежных видов) CropBox может быть недоступен
            }
        }

        /// <summary>
        /// Возвращает приоритет сортировки видов для упорядоченного создания листов по ГОСТ Р 21.1101:
        /// 1) Планы этажей (по возрастанию отметки уровня)
        /// 2) Планы потолков / зон
        /// 3) Фасады
        /// 4) Разрезы
        /// 5) 3D-виды и узлы
        /// </summary>
        public static (int TypeOrder, double Elevation, string Name) GetViewSortKey(View view)
        {
            int typeOrder = view.ViewType switch
            {
                ViewType.FloorPlan => 1,
                ViewType.EngineeringPlan => 2,
                ViewType.AreaPlan => 3,
                ViewType.CeilingPlan => 4,
                ViewType.Section => 5,
                ViewType.Elevation => 6,
                ViewType.ThreeD => 7,
                ViewType.Detail => 8,
                ViewType.DraftingView => 9,
                _ => 10
            };

            double elev = view.GenLevel?.Elevation ?? 0.0;
            return (typeOrder, elev, view.Name ?? string.Empty);
        }

        /// <summary>
        /// Вычисляет точные координаты левого нижнего угла видимой рамки листа А3 (420х297 мм) в системных единицах (футах).
        /// В семействах основной надписи ГОСТ (Форма 3) точка вставки семейства (0,0) — это правый нижний угол листа.
        /// Поэтому левый нижний угол листа А3 находится в точке X = (точка_вставки.X - 420 мм), Y = точка_вставки.Y.
        /// </summary>
        public static (double OriginXFt, double OriginYFt) GetSheetOriginFt(Document doc, ViewSheet sheet)
        {
            const double a3WidthFt = SheetWidthMm * MmToFeet; // 420 мм в футах

            if (sheet == null) return (-a3WidthFt, 0.0);

            Element? titleBlock = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            double baseX = 0.0;
            double baseY = 0.0;
            if (titleBlock?.Location is LocationPoint lp && lp.Point != null)
            {
                baseX = lp.Point.X;
                baseY = lp.Point.Y;
            }

            // Проверяем фактический контур Outline листа, если он определен в модели
            try
            {
                BoundingBoxUV? outline = sheet.Outline;
                if (outline != null)
                {
                    double minU = outline.Min.U;
                    double maxU = outline.Max.U;
                    double widthFt = Math.Abs(maxU - minU);

                    // Если контур соответствует ширине листа А3 (420 мм с допуском 30 мм)
                    if (Math.Abs(widthFt - a3WidthFt) < (30.0 * MmToFeet))
                    {
                        return (minU, outline.Min.V);
                    }
                }
            }
            catch { }

            // По умолчанию для семейств ГОСТ РК / РФ: точка вставки находится в правом нижнем углу листа.
            // Левый нижний угол листа А3 смещен влево на ширину листа (420 мм).
            return (baseX - a3WidthFt, baseY);
        }

        /// <summary>
        /// Вычисляет оптимальный прямоугольник на листе А3 (в футах) для размещения основного чертежа
        /// с учетом штампа Форма 3 (185х55 мм справа снизу), таблиц в правой колонке, координационных осей
        /// и места под верхний заголовок по ГОСТ 21.501-93.
        /// </summary>
        public static (double MinXFt, double MinYFt, double MaxXFt, double MaxYFt) ComputeOptimalViewportZoneFt(
            double originXFt,
            double originYFt,
            double rightColumnReservedWidthMm,
            bool hasScheduleOnRight)
        {
            const double titleReservedHeightMm = 15.0; // Место сверху под заголовок "План 1 этажа на отм. ±0.000 (М 1:200)"

            double topYFt = originYFt + ((SheetHeightMm - MarginTopMm - InnerPaddingMm - titleReservedHeightMm) * MmToFeet);

            // Случай 1: Компактная правая колонка НАД штампом (Разрезы и Фасады — ширина таблицы 115..140 мм < 185 мм штампа).
            // Размещаем видовой экран выше уровня штампа (Y >= 66 мм), благодаря чему по горизонтали свободна вся ширина до самой таблицы (260+ мм)!
            if (rightColumnReservedWidthMm > 5.0 && rightColumnReservedWidthMm < StampWidthMm - 15.0)
            {
                double leftXFt = originXFt + ((MarginLeftMm + InnerPaddingMm + 4.0) * MmToFeet);
                double rightXFt = originXFt + ((SheetWidthMm - MarginRightMm - rightColumnReservedWidthMm - InnerPaddingMm) * MmToFeet);
                double bottomYFt = originYFt + ((MarginBottomMm + StampHeightMm + InnerPaddingMm + 6.0) * MmToFeet);
                return (leftXFt, bottomYFt, rightXFt, topYFt);
            }

            // Случай 2: Правая колонка занята широкой экспликацией помещений (на ширину штампа 185 мм) — Планы этажей.
            // Слева и снизу оставляем место под вертикальную (А..Г) и горизонтальную (1..N) цепочки координационных осей Ø8 мм.
            if (hasScheduleOnRight || rightColumnReservedWidthMm >= StampWidthMm - 15.0)
            {
                double reservedRightMm = Math.Max(StampWidthMm, rightColumnReservedWidthMm);
                double leftXFt = originXFt + ((MarginLeftMm + 16.0) * MmToFeet);
                double rightXFt = originXFt + ((SheetWidthMm - MarginRightMm - reservedRightMm - InnerPaddingMm) * MmToFeet);
                double bottomYFt = originYFt + ((MarginBottomMm + 20.0) * MmToFeet);

                double minAllowedWidthFt = 165.0 * MmToFeet;
                if (rightXFt - leftXFt < minAllowedWidthFt)
                {
                    rightXFt = leftXFt + minAllowedWidthFt;
                }

                return (leftXFt, bottomYFt, rightXFt, topYFt);
            }

            // Случай 3: Таблиц справа нет (3D-виды, обзорные схемы) — вся ширина листа А3 над штампом
            {
                double leftXFt = originXFt + ((MarginLeftMm + InnerPaddingMm) * MmToFeet);
                double rightXFt = originXFt + ((SheetWidthMm - MarginRightMm - InnerPaddingMm) * MmToFeet);
                double bottomYFt = originYFt + ((MarginBottomMm + StampHeightMm + InnerPaddingMm + 4.0) * MmToFeet);
                return (leftXFt, bottomYFt, rightXFt, topYFt);
            }
        }

        /// <summary>
        /// Возвращает нормативный ряд масштабов по ГОСТ 21.501-93 (Таблица 1) / ГОСТ 2.302-68 / ГОСТ 21.508-93
        /// и целевой коэффициент заполнения рабочей зоны для конкретного типа вида, чтобы планы этажей,
        /// планы кровли, продольные/торцевые фасады и продольные/поперечные разрезы получали свои правильные разные масштабы ГОСТ.
        /// </summary>
        public static (int[] AllowedScales, double FillFactor, int MinScale) GetGostScaleRulesForView(View view)
        {
            string nameLower = (view.Name ?? string.Empty).ToLowerInvariant();

            // 1. Генеральный план (ГОСТ 21.508-93 п. 3.2: 1:200, 1:500, 1:1000, 1:2000)
            if (nameLower.StartsWith("гп_") || nameLower.Contains("генплан") || nameLower.Contains("генеральный план"))
            {
                return (GostSitePlanScales, 0.85, 200);
            }

            // 2. План кровли и планы потолков (ГОСТ 21.501-93 Таблица 1: обзорные масштабы 1:200, 1:400)
            if (view.ViewType == ViewType.CeilingPlan || nameLower.Contains("кровл") || nameLower.Contains("крыш"))
            {
                return (GostRoofAndCeilingPlanScales, 0.45, 200);
            }

            // 3. Поэтажные планы (ГОСТ 21.501-93 Таблица 1 п. 1: 1:100, 1:200, 1:400)
            if (view.ViewType is ViewType.FloorPlan or ViewType.AreaPlan or ViewType.EngineeringPlan)
            {
                return (GostFloorPlanScales, 0.56, 100);
            }

            // 4. Разрезы здания (ГОСТ 21.501-93 Таблица 1 п. 2 и ГОСТ 2.302-68: 1:20, 1:25, 1:40, 1:50, 1:75, 1:100, 1:200)
            // Поперечный разрез 2–2 выполняется крупнее (до 1:40 / 1:50 / 1:75), продольный разрез 1–1 — 1:50 / 1:75 / 1:100
            if (view.ViewType == ViewType.Section)
            {
                bool isTransverse = Regex.IsMatch(nameLower, @"(?:2[-–—]2|2\b|поперечн)");
                return isTransverse
                    ? (GostSectionScales, 0.96, 25)
                    : (GostSectionScales, 0.88, 40);
            }

            // 5. Фасады здания (ГОСТ 21.501-93 Таблица 1 п. 3 и ГОСТ 2.302-68: 1:40, 1:50, 1:75, 1:100, 1:200, 1:400)
            // Торцевые фасады (Восточный/Западный, в осях А–Г) компактнее и получают более крупный масштаб, чем продольные (1–5)
            if (view.ViewType == ViewType.Elevation)
            {
                bool isSideFacade = nameLower.Contains("вост") || nameLower.Contains("запад") ||
                                    nameLower.Contains("east") || nameLower.Contains("west") ||
                                    nameLower.Contains("а–г") || nameLower.Contains("г–а") ||
                                    nameLower.Contains("а-г") || nameLower.Contains("г-а");
                return isSideFacade
                    ? (GostFacadeScales, 0.85, 50)
                    : (GostFacadeScales, 0.72, 75);
            }

            // 6. Узлы и фрагменты (ГОСТ 21.501-93 Таблица 1 п. 4–5: 1:5, 1:10, 1:20, 1:25, 1:50)
            if (view.ViewType is ViewType.Detail or ViewType.DraftingView)
            {
                return (GostDetailScales, 0.85, 5);
            }

            return (GostStandardScales, 0.80, 50);
        }

        /// <summary>
        /// Предварительно рассчитывает индивидуальный стандартный масштаб ГОСТ для любого вида модели
        /// (используется в интерактивном окне выбора видов перед созданием листов).
        /// </summary>
        public static int EstimateGostScaleForView(Document doc, View view, BoundingBoxXYZ? buildingBox)
        {
            (int[] allowedScales, double fillFactor, int minScale) = GetGostScaleRulesForView(view);

            double bWidthFt = buildingBox != null ? Math.Max(20.0 * MmToFeet, buildingBox.Max.X - buildingBox.Min.X) : 18000.0 * MmToFeet;
            double bLengthFt = buildingBox != null ? Math.Max(20.0 * MmToFeet, buildingBox.Max.Y - buildingBox.Min.Y) : 12000.0 * MmToFeet;
            double bHeightFt = buildingBox != null ? Math.Max(12.0 * MmToFeet, buildingBox.Max.Z - buildingBox.Min.Z) : 6500.0 * MmToFeet;

            double longSpanFt = Math.Max(bWidthFt, bLengthFt);
            double shortSpanFt = Math.Min(bWidthFt, bLengthFt);

            string nameLower = (view.Name ?? string.Empty).ToLowerInvariant();
            double modelWFt;
            double modelHFt;
            double targetWFt;
            double targetHFt;

            if (view.ViewType == ViewType.Section)
            {
                bool isTransverse = Regex.IsMatch(nameLower, @"(?:2[-–—]2|2\b|поперечн)");
                modelWFt = (isTransverse ? shortSpanFt * 0.85 : longSpanFt) + (1200.0 * MmToFeet);
                modelHFt = bHeightFt + (1400.0 * MmToFeet);
                targetWFt = 261.0 * MmToFeet;
                targetHFt = 206.0 * MmToFeet;
            }
            else if (view.ViewType == ViewType.Elevation)
            {
                bool isSideFacade = nameLower.Contains("вост") || nameLower.Contains("запад") ||
                                    nameLower.Contains("east") || nameLower.Contains("west") ||
                                    nameLower.Contains("а–г") || nameLower.Contains("г–а");
                modelWFt = (isSideFacade ? shortSpanFt * 0.88 : longSpanFt) + (1500.0 * MmToFeet);
                modelHFt = bHeightFt + (1200.0 * MmToFeet);
                targetWFt = 261.0 * MmToFeet;
                targetHFt = 206.0 * MmToFeet;
            }
            else
            {
                modelWFt = longSpanFt + (1900.0 * MmToFeet);
                modelHFt = shortSpanFt + (1900.0 * MmToFeet);
                targetWFt = 186.0 * MmToFeet;
                targetHFt = 250.0 * MmToFeet;
            }

            double reqScaleX = modelWFt / (targetWFt * fillFactor);
            double reqScaleY = modelHFt / (targetHFt * fillFactor);
            double minReqScale = Math.Max(reqScaleX, reqScaleY);

            foreach (int candidate in allowedScales)
            {
                if (candidate >= minScale && candidate >= minReqScale)
                {
                    return candidate;
                }
            }

            return allowedScales[^1];
        }

        /// <summary>
        /// Устанавливает выбранный пользователем масштаб (либо автоматически подбирает стандартный масштаб по ГОСТ 21.501-93 / ГОСТ 2.302-68,
        /// если выбран режим «Авто») для видового экрана (Viewport), а затем центрирует его в целевой зоне листа А3.
        /// </summary>
        public static int FitAndCenterViewportInZone(
            Document doc,
            View view,
            Viewport viewport,
            double zoneMinXFt,
            double zoneMinYFt,
            double zoneMaxXFt,
            double zoneMaxYFt,
            int? userForcedScale = null)
        {
            double targetWidthFt = Math.Max(50.0 * MmToFeet, zoneMaxXFt - zoneMinXFt);
            double targetHeightFt = Math.Max(50.0 * MmToFeet, zoneMaxYFt - zoneMinYFt);

            // Скрываем стандартную длинную подпись под видовым экраном ("Название на листе"),
            // чтобы она не смещала габаритную рамку и не налезала на основную надпись
            TryDisableViewportTitleLabel(doc, viewport);
            doc.Regenerate();

            int chosenScale;

            // Если пользователь явно выбрал масштаб в диалоговом окне (например, М 1:100, М 1:50, М 1:200, М 1:500) —
            // устанавливаем строго выбранный пользователем масштаб без перезаписи!
            if (userForcedScale is > 0)
            {
                chosenScale = userForcedScale.Value;
                TrySetViewScale(view, chosenScale);
                doc.Regenerate();
            }
            else
            {
                (int[] allowedScales, double fillFactor, int minAllowedScale) = GetGostScaleRulesForView(view);

                // 1. Определяем реальные модельные габариты вида строго по его области подрезки (CropBox)
                double modelWidthFt = 0.0;
                double modelHeightFt = 0.0;

                if (view.CropBoxActive && view.CropBox != null)
                {
                    BoundingBoxXYZ cb = view.CropBox;
                    modelWidthFt = Math.Abs(cb.Max.X - cb.Min.X);
                    modelHeightFt = Math.Abs(cb.Max.Y - cb.Min.Y);

                    string nameLower = (view.Name ?? string.Empty).ToLowerInvariant();
                    if (view.ViewType == ViewType.Section && Regex.IsMatch(nameLower, @"(?:2[-–—]2|2\b|поперечн)"))
                    {
                        modelWidthFt *= 0.82;
                    }
                    else if (view.ViewType == ViewType.Elevation &&
                             (nameLower.Contains("вост") || nameLower.Contains("запад") ||
                              nameLower.Contains("east") || nameLower.Contains("west")))
                    {
                        modelWidthFt *= 0.86;
                    }
                }
                else
                {
                    Outline initialBox = viewport.GetBoxOutline();
                    int currentScale = Math.Max(1, view.Scale);
                    double boxPaperWidthFt = Math.Abs(initialBox.MaximumPoint.X - initialBox.MinimumPoint.X);
                    double boxPaperHeightFt = Math.Abs(initialBox.MaximumPoint.Y - initialBox.MinimumPoint.Y);

                    if (boxPaperWidthFt > 1e-4 && boxPaperHeightFt > 1e-4)
                    {
                        modelWidthFt = boxPaperWidthFt * currentScale;
                        modelHeightFt = boxPaperHeightFt * currentScale;
                    }
                }

                // 2. Выбираем оптимальный стандартный масштаб ГОСТ из нормативного ряда данного типа чертежа
                chosenScale = Math.Max(minAllowedScale, view.Scale);
                if (modelWidthFt > 1e-3 && modelHeightFt > 1e-3)
                {
                    double requiredScaleX = modelWidthFt / (targetWidthFt * fillFactor);
                    double requiredScaleY = modelHeightFt / (targetHeightFt * fillFactor);
                    double minRequiredScale = Math.Max(requiredScaleX, requiredScaleY);

                    chosenScale = allowedScales[^1];
                    foreach (int candidate in allowedScales)
                    {
                        if (candidate >= minAllowedScale && candidate >= minRequiredScale)
                        {
                            chosenScale = candidate;
                            break;
                        }
                    }

                    TrySetViewScale(view, chosenScale);
                    doc.Regenerate();
                }

                // 3. Контрольная проверка по фактическому контуру после смены масштаба
                for (int step = 0; step < 4; step++)
                {
                    Outline checkOutline = viewport.GetBoxOutline();
                    double wFt = Math.Abs(checkOutline.MaximumPoint.X - checkOutline.MinimumPoint.X);
                    double hFt = Math.Abs(checkOutline.MaximumPoint.Y - checkOutline.MinimumPoint.Y);

                    if (wFt <= targetWidthFt * 1.02 && hFt <= targetHeightFt * 1.02)
                    {
                        break;
                    }

                    int nextScale = allowedScales.FirstOrDefault(s => s > view.Scale);
                    if (nextScale <= 0)
                    {
                        nextScale = GostStandardScales.FirstOrDefault(s => s > view.Scale);
                    }
                    if (nextScale <= 0 || !TrySetViewScale(view, nextScale))
                    {
                        break;
                    }

                    chosenScale = nextScale;
                    doc.Regenerate();
                }
            }

            // 4. Двухпроходное математическое центрирование по фактическому контуру GetBoxOutline()
            XYZ targetCenter = new XYZ(
                (zoneMinXFt + zoneMaxXFt) / 2.0,
                (zoneMinYFt + zoneMaxYFt) / 2.0,
                0.0);

            for (int pass = 0; pass < 2; pass++)
            {
                Outline finalOutline = viewport.GetBoxOutline();
                XYZ actualOutlineCenter = new XYZ(
                    (finalOutline.MinimumPoint.X + finalOutline.MaximumPoint.X) / 2.0,
                    (finalOutline.MinimumPoint.Y + finalOutline.MaximumPoint.Y) / 2.0,
                    0.0);

                XYZ delta = targetCenter - actualOutlineCenter;
                if (delta.GetLength() > 0.2 * MmToFeet)
                {
                    XYZ currentBoxCenter = viewport.GetBoxCenter();
                    viewport.SetBoxCenter(currentBoxCenter + delta);
                    doc.Regenerate();
                }
            }

            return chosenScale > 0 ? chosenScale : (view.Scale > 0 ? view.Scale : 100);
        }

        public static bool TrySetViewScale(View view, int scale)
        {
            if (scale <= 0)
            {
                return false;
            }

            try
            {
                // Если шаблон вида блокирует изменение масштаба, отвязываем шаблон от вида
                // (графические переопределения и видимости категорий от шаблона остаются активными в виде)
                if (view.ViewTemplateId != ElementId.InvalidElementId)
                {
                    try
                    {
                        view.ViewTemplateId = ElementId.InvalidElementId;
                    }
                    catch
                    {
                    }
                }

                if (view.Scale != scale)
                {
                    view.Scale = scale;
                }
                return true;
            }
            catch
            {
                try
                {
                    Parameter? p = view.get_Parameter(BuiltInParameter.VIEW_SCALE);
                    if (p != null && !p.IsReadOnly)
                    {
                        p.Set(scale);
                        return true;
                    }
                }
                catch
                {
                }
                return false;
            }
        }

        /// <summary>
        /// Настраивает транзакцию Revit на автоматическое подавление всплывающих предупреждений
        /// (например, «Выделенные перекрытия пересекаются» и др.), чтобы окна ошибок не прерывали работу плагина.
        /// </summary>
        public static void ConfigureSilentTransaction(Transaction tx)
        {
            FailureHandlingOptions options = tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new SilentFailuresPreprocessor());
            options.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(options);
        }

        private sealed class SilentFailuresPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
                if (failures.Count == 0)
                {
                    return FailureProcessingResult.Continue;
                }

                foreach (FailureMessageAccessor fma in failures)
                {
                    FailureSeverity severity = fma.GetSeverity();
                    if (severity == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(fma);
                    }
                    else if (fma.HasResolutions())
                    {
                        try
                        {
                            failuresAccessor.ResolveFailure(fma);
                        }
                        catch
                        {
                            // Пропускаем, если стандартное разрешение неприменимо
                        }
                    }
                }

                return FailureProcessingResult.Continue;
            }
        }

        /// <summary>
        /// Отключает стандартную выносную линию заголовка под видовым экраном ("Показать название" = Нет),
        /// чтобы она не сдвигала рамку видового экрана и не пересекала основную надпись листа.
        /// </summary>
        private static void TryDisableViewportTitleLabel(Document doc, Viewport viewport)
        {
            try
            {
                ElementId typeId = viewport.GetTypeId();
                if (typeId != ElementId.InvalidElementId && doc.GetElement(typeId) is ElementType vpType)
                {
                    Parameter? showLabelParam = vpType.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL);
                    if (showLabelParam is { IsReadOnly: false, StorageType: StorageType.Integer })
                    {
                        showLabelParam.Set(0);
                    }
                }
            }
            catch
            {
                // Игнорируем, если тип видового экрана заблокирован
            }
        }

        /// <summary>
        /// Удобная перегрузка для получения ГОСТ-заголовка вида без явной передачи Document.
        /// </summary>
        public static string GetGostStandardViewTitle(View view, int scale = 0)
        {
            return FormatGostViewTitle(view.Document, view, scale);
        }

        /// <summary>
        /// Формирует заголовок вида строго по ГОСТ 21.501-93 / СН РК и ГОСТ 21.101-97:
        /// - Планы: "План 1 этажа на отм. ±0.000 (М 1:200)", "План кровли на отм. +12.600 (М 1:200)"
        /// - Разрезы: "Разрез 1–1 (М 1:200)", "Разрез 2–2 (М 1:200)"
        /// - Фасады: "Фасад в осях 1–5 (М 1:200)", "Фасад в осях А–Г (М 1:200)"
        /// - Генплан: "Схема генерального плана (М 1:500)", "3D Модель генерального плана"
        /// </summary>
        public static string FormatGostViewTitle(Document doc, View view, int scale)
        {
            string scaleSuffix = scale > 0 ? $" (М 1:{scale})" : string.Empty;
            string rawName = view.Name ?? string.Empty;
            string cleanName = rawName;

            if (cleanName.StartsWith("АР_", StringComparison.OrdinalIgnoreCase) ||
                cleanName.StartsWith("АР-", StringComparison.OrdinalIgnoreCase) ||
                cleanName.StartsWith("ГП_", StringComparison.OrdinalIgnoreCase) ||
                cleanName.StartsWith("ГП-", StringComparison.OrdinalIgnoreCase))
            {
                cleanName = cleanName[3..].Trim();
            }

            // Убираем старые суффиксы масштаба, если они были в названии вида
            cleanName = Regex.Replace(cleanName, @"\s*\((?:М\s*)?1:\d+\)", "", RegexOptions.IgnoreCase).Trim();

            // 1. Планы этажей, потолков, зонирования
            if (view.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.AreaPlan or ViewType.EngineeringPlan)
            {
                Level? level = view.GenLevel
                    ?? (view.LevelId != ElementId.InvalidElementId ? doc.GetElement(view.LevelId) as Level : null);

                double elevationM = 0.0;
                if (level != null)
                {
                    elevationM = UnitUtils.ConvertFromInternalUnits(level.Elevation, UnitTypeId.Meters);
                }

                string elevStr;
                if (Math.Abs(elevationM) < 0.001)
                {
                    elevStr = "±0.000";
                }
                else if (elevationM > 0)
                {
                    elevStr = $"+{elevationM:F3}".Replace(',', '.');
                }
                else
                {
                    elevStr = $"-{Math.Abs(elevationM):F3}".Replace(',', '.');
                }

                string lower = cleanName.ToLowerInvariant();
                string levelLower = level?.Name.ToLowerInvariant() ?? string.Empty;

                string planPrefix = view.ViewType == ViewType.CeilingPlan ? "План потолка" : "План";

                if (lower.Contains("кровл") || levelLower.Contains("кровл"))
                {
                    return $"План кровли на отм. {elevStr}{scaleSuffix}";
                }
                if (lower.Contains("подвал") || levelLower.Contains("подвал"))
                {
                    return $"{planPrefix} подвала на отм. {elevStr}{scaleSuffix}";
                }
                if (lower.Contains("цокол") || levelLower.Contains("цокол"))
                {
                    return $"{planPrefix} цокольного этажа на отм. {elevStr}{scaleSuffix}";
                }
                if (lower.Contains("мансард") || levelLower.Contains("мансард"))
                {
                    return $"{planPrefix} мансарды на отм. {elevStr}{scaleSuffix}";
                }

                // Ищем номер этажа в названии вида или уровня
                Match mFloor = Regex.Match(cleanName, @"(?:(\d+)\s*(?:-?й|-?ой|-?ий|-?ый)?\s*этаж)|(?:этаж[^\d]*(\d+))", RegexOptions.IgnoreCase);
                if (!mFloor.Success && level != null)
                {
                    mFloor = Regex.Match(level.Name, @"(?:(\d+)\s*(?:-?й|-?ой|-?ий|-?ый)?\s*этаж)|(?:этаж[^\d]*(\d+))|(?:\b(\d+)\b)", RegexOptions.IgnoreCase);
                }
                if (!mFloor.Success)
                {
                    mFloor = Regex.Match(cleanName, @"\b(\d+)\b");
                }

                if (mFloor.Success)
                {
                    string num = !string.IsNullOrEmpty(mFloor.Groups[1].Value) ? mFloor.Groups[1].Value :
                                 !string.IsNullOrEmpty(mFloor.Groups[2].Value) ? mFloor.Groups[2].Value :
                                 !string.IsNullOrEmpty(mFloor.Groups[3].Value) ? mFloor.Groups[3].Value :
                                 mFloor.Value;

                    return $"{planPrefix} {num} этажа на отм. {elevStr}{scaleSuffix}";
                }

                // Если явного номера нет, используем очищенное название
                string baseTitle = cleanName.StartsWith("План", StringComparison.OrdinalIgnoreCase)
                    ? cleanName
                    : $"{planPrefix} {cleanName}";

                return $"{baseTitle} на отм. {elevStr}{scaleSuffix}";
            }

            // 2. Разрезы
            if (view.ViewType == ViewType.Section)
            {
                Match mSec = Regex.Match(cleanName, @"(?:Разрез|Сечение|Section)?\s*([0-9А-Яа-яA-Za-z]+(?:\s*[-–—]\s*[0-9А-Яа-яA-Za-z]+)?)", RegexOptions.IgnoreCase);
                string mark = mSec.Success && !string.IsNullOrWhiteSpace(mSec.Groups[1].Value)
                    ? Regex.Replace(mSec.Groups[1].Value.Trim(), @"\s*[-–—]\s*", "–")
                    : cleanName;

                if (mark.Length == 1 && char.IsLetterOrDigit(mark[0]))
                {
                    mark = $"{mark}–{mark}";
                }

                return $"Разрез {mark}{scaleSuffix}";
            }

            // 3. Фасады (по ГОСТ 21.501-93 п. 5.3.2 — наименование по крайним координационным осям слева направо)
            if (view.ViewType == ViewType.Elevation)
            {
                (string numStart, string numEnd, string letStart, string letEnd) = GetExtremeGridLabels(doc);
                string lower = cleanName.ToLowerInvariant();

                if (lower.Contains("южн") || lower.Contains("south") || lower.Contains("главн"))
                {
                    return $"Фасад в осях {numStart}–{numEnd}{scaleSuffix}";
                }
                if (lower.Contains("север") || lower.Contains("north") || lower.Contains("задн") || lower.Contains("двор"))
                {
                    return $"Фасад в осях {numEnd}–{numStart}{scaleSuffix}";
                }
                if (lower.Contains("вост") || lower.Contains("east") || lower.Contains("прав"))
                {
                    return $"Фасад в осях {letStart}–{letEnd}{scaleSuffix}";
                }
                if (lower.Contains("запад") || lower.Contains("west") || lower.Contains("лев"))
                {
                    return $"Фасад в осях {letEnd}–{letStart}{scaleSuffix}";
                }

                Match mAxes = Regex.Match(cleanName, @"([0-9А-Яа-яA-Za-z]+)\s*[-–—]\s*([0-9А-Яа-яA-Za-z]+)");
                if (mAxes.Success)
                {
                    string a1 = mAxes.Groups[1].Value.ToUpperInvariant();
                    string a2 = mAxes.Groups[2].Value.ToUpperInvariant();
                    if (a1 == "1" && (a2 == "A" || a2 == "А"))
                    {
                        return $"Фасад в осях {numStart}–{numEnd}{scaleSuffix}";
                    }
                    return $"Фасад в осях {a1}–{a2}{scaleSuffix}";
                }

                return $"Фасад в осях {numStart}–{numEnd} ({cleanName}){scaleSuffix}";
            }

            // 4. Генеральный план и 3D
            if (rawName.StartsWith("ГП_", StringComparison.OrdinalIgnoreCase) ||
                cleanName.Contains("Генплан", StringComparison.OrdinalIgnoreCase) ||
                cleanName.Contains("Генеральный план", StringComparison.OrdinalIgnoreCase))
            {
                if (view is View3D)
                {
                    return "3D Модель генерального плана";
                }
                return $"Схема генерального плана{scaleSuffix}";
            }

            if (view is View3D)
            {
                return "3D Вид здания";
            }

            if (view.ViewType is ViewType.Detail or ViewType.DraftingView)
            {
                return $"Узел {cleanName}{scaleSuffix}";
            }

            return $"{cleanName}{scaleSuffix}";
        }

        public static (string NumStart, string NumEnd, string LetStart, string LetEnd) GetExtremeGridLabels(Document doc)
        {
            try
            {
                List<string> gridNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .Select(g => g.Name?.Trim() ?? string.Empty)
                    .Where(n => !string.IsNullOrEmpty(n) && !GostGridManager.IsExtraUnusedGridName(n))
                    .ToList();

                List<(int Val, string Name)> numGrids = [];
                List<string> letGrids = [];

                foreach (string g in gridNames)
                {
                    if (int.TryParse(Regex.Match(g, @"^\d+").Value, out int n))
                    {
                        numGrids.Add((n, g));
                    }
                    else if (char.IsLetter(g[0]))
                    {
                        letGrids.Add(g.ToUpperInvariant());
                    }
                }

                numGrids = numGrids.OrderBy(x => x.Val).ToList();
                letGrids = letGrids.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();

                string numStart = numGrids.Count >= 2 ? numGrids.First().Name : "1";
                string numEnd = numGrids.Count >= 2 ? numGrids.Last().Name : "5";
                string letStart = letGrids.Count >= 2 ? letGrids.First() : "А";
                string letEnd = letGrids.Count >= 2 ? letGrids.Last() : "Г";

                return (numStart, numEnd, letStart, letEnd);
            }
            catch
            {
                return ("1", "5", "А", "Г");
            }
        }

        /// <summary>
        /// Создает или обновляет оформленный по ГОСТ заголовок вида (TextNote 5мм ISOCPEUR)
        /// непосредственно НАД видовым экраном на листе, центрируя его строго по ширине чертежа.
        /// Также синхронизирует параметр вида "Заголовок на листе" (VIEW_DESCRIPTION).
        /// </summary>
        public static TextNote? CreateOrUpdateGostViewTitleNote(
            Document doc,
            ViewSheet sheet,
            Viewport viewport,
            View view,
            int scale)
        {
            try
            {
                string gostTitle = FormatGostViewTitle(doc, view, scale);

                // Синхронизируем параметр вида "Заголовок на листе"
                Parameter? viewDescParam = view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
                if (viewDescParam is { IsReadOnly: false, StorageType: StorageType.String })
                {
                    viewDescParam.Set(gostTitle);
                }

                Outline box = viewport.GetBoxOutline();
                double centerXFt = (box.MinimumPoint.X + box.MaximumPoint.X) / 2.0;
                double topYFt = box.MaximumPoint.Y + (5.0 * MmToFeet); // 5 мм над верхним краем видового экрана

                // Не допускаем заезда заголовка выше верхней рамки листа
                double maxAllowedTopYFt = GetSheetOriginFt(doc, sheet).OriginYFt + ((SheetHeightMm - MarginTopMm - 7.0) * MmToFeet);
                if (topYFt > maxAllowedTopYFt)
                {
                    topYFt = maxAllowedTopYFt;
                }

                XYZ titleOrigin = new XYZ(centerXFt, topYFt, 0.0);

                ElementId textTypeId = GetOrCreateGostTitleTextNoteTypeId(doc);
                if (textTypeId == ElementId.InvalidElementId)
                {
                    return null;
                }

                // Удаляем старые заголовки этого вида на листе, чтобы они не двоились
                List<TextNote> existingSheetNotes = new FilteredElementCollector(doc, sheet.Id)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .Where(tn =>
                    {
                        XYZ pt = tn.Coord;
                        bool withinX = pt.X >= box.MinimumPoint.X - (20.0 * MmToFeet) &&
                                       pt.X <= box.MaximumPoint.X + (20.0 * MmToFeet);
                        bool withinY = pt.Y >= box.MaximumPoint.Y - (10.0 * MmToFeet) &&
                                       pt.Y <= box.MaximumPoint.Y + (40.0 * MmToFeet);
                        string text = tn.Text ?? string.Empty;
                        bool looksLikeTitle = text.Contains("(М 1:") ||
                                              text.StartsWith("План ", StringComparison.OrdinalIgnoreCase) ||
                                              text.StartsWith("Разрез ", StringComparison.OrdinalIgnoreCase) ||
                                              text.StartsWith("Фасад ", StringComparison.OrdinalIgnoreCase) ||
                                              text.StartsWith("Схема ", StringComparison.OrdinalIgnoreCase) ||
                                              text.StartsWith("3D ", StringComparison.OrdinalIgnoreCase) ||
                                              text.StartsWith("Узел ", StringComparison.OrdinalIgnoreCase);
                        return withinX && withinY && looksLikeTitle;
                    })
                    .ToList();

                foreach (TextNote oldNote in existingSheetNotes)
                {
                    try
                    {
                        doc.Delete(oldNote.Id);
                    }
                    catch
                    {
                        // Игнорируем
                    }
                }

                TextNoteOptions options = new TextNoteOptions(textTypeId)
                {
                    HorizontalAlignment = HorizontalTextAlignment.Center,
                    Rotation = 0.0
                };

                return TextNote.Create(doc, sheet.Id, titleOrigin, gostTitle, options);
            }
            catch
            {
                return null;
            }
        }

        private static ElementId GetOrCreateGostTitleTextNoteTypeId(Document doc)
        {
            const string typeName = "ГП_Заголовок_5мм";
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
                .FirstOrDefault(t => t.Name.Contains("5") || t.Name.Contains("ISOCPEUR") || t.Name.Contains("ГОСТ"))
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
                newType.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(5.0 * MmToFeet);
                newType.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set("ISOCPEUR");
                newType.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)?.Set(1);
                newType.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1); // 1 = Transparent
                return newType.Id;
            }
            catch
            {
                return baseType.Id;
            }
        }
    }
}

