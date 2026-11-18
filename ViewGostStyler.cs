using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitViewPlacer
{
    /// <summary>
    /// Элемент высотной отметки по ГОСТ 21.101-97 (Форма 1).
    /// </summary>
    public sealed record GostElevationItem(
        double ElevationFt,
        string FormattedMeters,
        string Label,
        int Priority = 0);

    /// <summary>
    /// Элемент координационной оси для проекции на фасад/разрез.
    /// </summary>
    public sealed record GostGridItem(
        double U,
        string Name);

    /// <summary>
    /// Профессиональный стилизатор архитектурных видов здания по ГОСТ 21.501-93, ГОСТ 21.101-97 и СН РК.
    /// Все аннотационные элементы вычерчиваются непосредственно ВНУТРИ самого вида (ViewSection):
    /// 1. Приведение графики к СПДС (HLR, Fine, отключение фона и теней, скрытие шума).
    /// 2. Координационные оси внизу чертежа в кружках Ø8 мм с цепочкой габаритных размеров.
    /// 3. Нормативная линия земли с диагональной штриховкой грунта под углом 45°.
    /// 4. Двусторонняя цепочка наружных высотных отметок (слева и справа).
    /// 5. Высотные отметки конька кровли и дымовой трубы в верхней точке фасада/разреза.
    /// 6. Для разрезов: внутренние отметки на плитах перекрытий (±0.000, +3.300), вертикальные цепочки высот этажей
    ///    и выноски многослойных конструкций («этажерки» пирогов кровли и полов по ГОСТ 21.501-93 Форма 4).
    /// 7. Автоматическая подрезка CropBox без видимой рамки.
    /// </summary>
    public static class ViewGostStyler
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const string GostAnnotationComment = "GOST_MARK";
        private const string GostGroundComment = "GOST_GROUND";

        /// <summary>
        /// Главный метод: полностью приводит архитектурный вид в порядок по ГОСТ РК и СН РК.
        /// </summary>
        public static void PrepareArchitecturalViewForGost(
            Document doc,
            View view,
            BoundingBoxXYZ? buildingWorldBox)
        {
            if (view.IsTemplate) return;

            // 1. Отключаем блокирующий шаблон вида, если он мешает настройке графики
            if (view.ViewTemplateId != ElementId.InvalidElementId)
            {
                try { view.ViewTemplateId = ElementId.InvalidElementId; } catch { }
            }

            // 2. Настраиваем четкую СПДС графику и скрываем визуальный шум
            ApplyGraphicDisplayAndClutterRemoval(doc, view);

            // 3. Вычисляем габариты здания, если не переданы
            BoundingBoxXYZ? bbox = buildingWorldBox ?? SheetLayoutEngine.ComputeBuildingWorldBoundingBox(doc);
            if (bbox == null) return;

            // Приводим маркировку существующих осей к ГОСТ (1, 2, 3... слева направо, А, Б, В... снизу вверх),
            // скрываем лишние/неиспользуемые оси на виде и оформляем кружки и размерные цепочки осей
            GostGridManager.RenumberExistingGridsByGost(doc, bbox);
            GostGridManager.HideUnusedGridsInView(doc, view, bbox);

            // 4. Оформление разрезов и фасадов (высотные отметки, оси, пироги и земля внутри вида)
            if (view is ViewSection viewSection && viewSection.ViewType is ViewType.Elevation or ViewType.Section)
            {
                // Ограничиваем секущую глубину (FarClip), чтобы далекие объекты генплана не просвечивали
                ConfigureFarClip(viewSection);

                // Очищаем старые аннотации плагина в виде (для 100% идемпотентности)
                CleanPreviousGostAnnotations(doc, viewSection);

                // Вычерчиваем все элементы ГОСТ оформления внутри вида
                DecorateSectionOrElevationView(doc, viewSection, bbox);
            }
            else if (view.ViewType == ViewType.FloorPlan)
            {
                // Если в проекте вообще нет координационных осей, подрезаем CropBox по габаритам здания
                bool hasAnyGrids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).GetElementCount() > 0;
                if (!hasAnyGrids && view.CropBox != null)
                {
                    SheetLayoutEngine.FitViewCropBoxToWorldBox(view, bbox, 1800.0);
                }
            }
        }

        /// <summary>
        /// Настраивает графический СПДС режим по ГОСТ и скрывает все категории визуального мусора.
        /// </summary>
        private static void ApplyGraphicDisplayAndClutterRemoval(Document doc, View view)
        {
            // ГОСТ: Режим «Скрытые линии» (черно-белый рабочий чертеж), высокая детализация
            try { view.DisplayStyle = DisplayStyle.HLR; } catch { }
            try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }

            // Отключаем фон (небо/градиент)
            try
            {
                view.SetBackground(null);
            }
            catch { }

            // Отключаем тени
            try
            {
                view.SunAndShadowSettings.Visible = false;
            }
            catch { }

            // Включаем подрезку, но скрываем саму прямоугольную рамку подрезки на листе
            try
            {
                view.CropBoxActive = true;
                view.CropBoxVisible = false;
            }
            catch { }

            // Включаем аннотационную подрезку
            try
            {
                Parameter? annotCrop = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (annotCrop is { IsReadOnly: false })
                {
                    annotCrop.Set(1);
                }
            }
            catch { }

            // Скрываем категории визуального шума
            BuiltInCategory[] categoriesToHide =
            [
                BuiltInCategory.OST_SharedBasePoint,      // Точка съемки
                BuiltInCategory.OST_ProjectBasePoint,    // Базовая точка проекта
                BuiltInCategory.OST_Cameras,             // Камеры
                BuiltInCategory.OST_CLines,              // Опорные плоскости
                BuiltInCategory.OST_VolumeOfInterest,    // Области видимости (Scope Boxes)
                BuiltInCategory.OST_Site,                // Генплан
                BuiltInCategory.OST_Planting,            // Озеленение генплана
                BuiltInCategory.OST_Toposolid,           // Топосолиды
                BuiltInCategory.OST_Topography,          // Топография
                BuiltInCategory.OST_ElevationMarks,      // Маркеры фасадов
                BuiltInCategory.OST_Callouts,            // Выноски
                BuiltInCategory.OST_Viewers              // Вьюеры
            ];

            foreach (BuiltInCategory cat in categoriesToHide)
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

            // На фасадах и разрезах полностью скрываем оси и размеры (по ГОСТ оформлению по требованию пользователя)
            if (view.ViewType is ViewType.Elevation or ViewType.Section)
            {
                try
                {
                    ElementId gridCatId = new ElementId(BuiltInCategory.OST_Grids);
                    if (view.CanCategoryBeHidden(gridCatId))
                    {
                        view.SetCategoryHidden(gridCatId, true);
                    }
                }
                catch { }

                try
                {
                    ElementId dimCatId = new ElementId(BuiltInCategory.OST_Dimensions);
                    if (view.CanCategoryBeHidden(dimCatId))
                    {
                        view.SetCategoryHidden(dimCatId, true);
                    }
                }
                catch { }
            }

            // На фасадах скрываем маркеры чужих разрезов, чтобы не пересекали фасад
            if (view.ViewType == ViewType.Elevation)
            {
                try
                {
                    ElementId secCatId = new ElementId(BuiltInCategory.OST_Sections);
                    if (view.CanCategoryBeHidden(secCatId))
                    {
                        view.SetCategoryHidden(secCatId, true);
                    }
                }
                catch { }
            }

            // Скрываем перекрытия благоустройства генплана
            try
            {
                List<ElementId> siteFloors = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Floors)
                    .WhereElementIsNotElementType()
                    .Where(e => SheetLayoutEngine.IsSitePlanElement(doc, e))
                    .Select(e => e.Id)
                    .ToList();

                if (siteFloors.Count > 0)
                {
                    List<ElementId> hideable = siteFloors.Where(id => doc.GetElement(id) is { } el && el.CanBeHidden(view)).ToList();
                    if (hideable.Count > 0)
                    {
                        view.HideElements(hideable);
                    }
                }
            }
            catch { }

            // Скрываем DirectShape элементы генплана (деревья, фонари, скамейки, бордюры)
            try
            {
                List<ElementId> siteDs = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(DirectShape))
                    .WhereElementIsNotElementType()
                    .Where(e => SheetLayoutEngine.IsSitePlanElement(doc, e))
                    .Select(e => e.Id)
                    .ToList();

                if (siteDs.Count > 0)
                {
                    List<ElementId> hideableDs = siteDs.Where(id => doc.GetElement(id) is { } el && el.CanBeHidden(view)).ToList();
                    if (hideableDs.Count > 0)
                    {
                        view.HideElements(hideableDs);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Ограничивает глубину видимости разреза/фасада, чтобы разрез не засорялся фоном.
        /// </summary>
        private static void ConfigureFarClip(ViewSection viewSection)
        {
            try
            {
                Parameter? farClipActive = viewSection.get_Parameter(BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR);
                if (farClipActive is { IsReadOnly: false })
                {
                    farClipActive.Set(1);
                }

                Parameter? farClipOffset = viewSection.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                if (farClipOffset is { IsReadOnly: false })
                {
                    double curVal = farClipOffset.AsDouble();
                    double desiredFarFt = viewSection.ViewType == ViewType.Elevation ? 12000.0 * MmToFeet : 8000.0 * MmToFeet;
                    if (curVal > 25000.0 * MmToFeet || curVal < 1000.0 * MmToFeet)
                    {
                        farClipOffset.Set(desiredFarFt);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Вычерчивает полный комплект ГОСТ-элементов ВНУТРИ видового пространства разреза или фасада.
        /// </summary>
        private static void DecorateSectionOrElevationView(
            Document doc,
            ViewSection view,
            BoundingBoxXYZ buildingBox)
        {
            if (view.CropBox == null) return;

            Transform t = view.CropBox.Transform;
            XYZ rightDir = t.BasisX.Normalize();
            XYZ upDir = t.BasisY.Normalize(); // (0, 0, 1) для вертикальных проекций
            XYZ origin = t.Origin;

            // 1. Проецируем угловые точки габарита здания в систему координат вида (u, v)
            List<XYZ> corners =
            [
                new XYZ(buildingBox.Min.X, buildingBox.Min.Y, buildingBox.Min.Z),
                new XYZ(buildingBox.Max.X, buildingBox.Min.Y, buildingBox.Min.Z),
                new XYZ(buildingBox.Min.X, buildingBox.Max.Y, buildingBox.Min.Z),
                new XYZ(buildingBox.Max.X, buildingBox.Max.Y, buildingBox.Min.Z),
                new XYZ(buildingBox.Min.X, buildingBox.Min.Y, buildingBox.Max.Z),
                new XYZ(buildingBox.Max.X, buildingBox.Min.Y, buildingBox.Max.Z),
                new XYZ(buildingBox.Min.X, buildingBox.Max.Y, buildingBox.Max.Z),
                new XYZ(buildingBox.Max.X, buildingBox.Max.Y, buildingBox.Max.Z)
            ];

            double uMin = double.MaxValue;
            double uMax = double.MinValue;
            foreach (XYZ corner in corners)
            {
                double u = (corner - origin).DotProduct(rightDir);
                uMin = Math.Min(uMin, u);
                uMax = Math.Max(uMax, u);
            }

            double zBuildingMin = buildingBox.Min.Z;
            double zBuildingMax = buildingBox.Max.Z;

            int scale = Math.Max(1, view.Scale);
            double scaleFt = scale * MmToFeet;

            // 2. Стили линий и шрифты
            GraphicsStyle? wideStyle = GetWideLineStyle(doc);
            GraphicsStyle? thinStyle = GetThinLineStyle(doc);

            ElementId fontMarkId = GetOrCreateGostTextType(doc, 2.5, bold: true, "Отметки");
            ElementId fontLayerId = GetOrCreateGostTextType(doc, 2.1, bold: false, "ПирогСлой");
            ElementId fontAxisId = GetOrCreateGostTextType(doc, 3.0, bold: true, "ОсьМарка");
            ElementId fontDimId = GetOrCreateGostTextType(doc, 2.3, bold: false, "ОсьРазмер");

            // 3. Собираем ключевые уровни здания для высотных отметок
            List<GostElevationItem> markItems = CollectElevationItems(doc, zBuildingMin, zBuildingMax);
            if (markItems.Count == 0) return;

            // Определяем фактическую отметку земли
            double groundZFt = markItems.FirstOrDefault(m => m.Label.Contains("земл", StringComparison.OrdinalIgnoreCase))?.ElevationFt
                               ?? Math.Min(0.0, zBuildingMin);

            // 4. Вычерчиваем толстую нормативную линию земли с 45° штриховкой грунта
            DrawGostGroundLine(doc, view, origin, rightDir, upDir, uMin, uMax, groundZFt, scaleFt, wideStyle, thinStyle);

            // 5. Вычерчиваем высотные отметки строго в одну строчку вдоль здания
            DrawDualGostElevationMarks(doc, view, origin, rightDir, upDir, uMin, uMax, markItems, scaleFt, fontMarkId, thinStyle);

            // 6. Вычерчиваем используемые координационные оси проекта внизу разреза/фасада с размерной цепочкой
            DrawGostBottomAxesAndDimensions(doc, view, origin, rightDir, upDir, uMin, uMax, groundZFt, scaleFt, fontAxisId, fontDimId, thinStyle);

            // 7. Вычерчиваем высотные отметки конька кровли и трубы в верхней части чертежа
            DrawRoofRidgeAndChimneyMarks(doc, view, origin, rightDir, upDir, uMin, uMax, zBuildingMax, scaleFt, fontMarkId, thinStyle);

            // 8. Специфика архитектурных разрезов: внутренние отметки, вертикальные размеры и этажерки пирогов
            bool isSection = view.ViewType == ViewType.Section;
            if (isSection)
            {
                DrawSectionSpecificInteriorAnnotations(doc, view, origin, rightDir, upDir, uMin, uMax, groundZFt, zBuildingMax, markItems, scaleFt, fontMarkId, fontLayerId, thinStyle);
            }

            // 9. Корректируем границы CropBox вида, чтобы все элементы поместились без обрезки
            AdjustCropBoxToFitGostElements(view, t, origin, uMin, uMax, groundZFt, zBuildingMax, scaleFt, isSection);
        }

        /// <summary>
        /// Вычерчивает нормативную линию земли по ГОСТ 21.501-93 с диагональной штриховкой грунта под 45°.
        /// </summary>
        private static void DrawGostGroundLine(
            Document doc,
            ViewSection view,
            XYZ origin,
            XYZ rightDir,
            XYZ upDir,
            double uMin,
            double uMax,
            double groundZFt,
            double scaleFt,
            GraphicsStyle? wideStyle,
            GraphicsStyle? thinStyle)
        {
            try
            {
                double vGround = groundZFt - origin.Z;

                // Выступ линии земли за пределы здания: 25 мм на чертеже
                double groundExtFt = 25.0 * scaleFt;
                double uStart = uMin - groundExtFt;
                double uEnd = uMax + groundExtFt;

                XYZ pStart = origin + (uStart * rightDir) + (vGround * upDir);
                XYZ pEnd = origin + (uEnd * rightDir) + (vGround * upDir);

                if (pStart.DistanceTo(pEnd) < 0.005) return;

                // Толстая линия земли
                Line groundLine = Line.CreateBound(pStart, pEnd);
                DetailCurve curve = doc.Create.NewDetailCurve(view, groundLine);
                TagGostElement(curve, GostGroundComment);
                if (wideStyle != null) try { curve.LineStyle = wideStyle; } catch { }

                // Штриховка грунта под 45° вниз: шаг 3.5 мм, длина засечки 3.5 мм
                double hatchStep = 3.5 * scaleFt;
                double hatchLen = 3.5 * scaleFt;
                double hatchDx = hatchLen * 0.7071; // cos(45°)
                double hatchDy = hatchLen * 0.7071; // sin(45°)

                for (double u = uStart; u <= uEnd; u += hatchStep)
                {
                    XYZ pt1 = origin + (u * rightDir) + (vGround * upDir);
                    XYZ pt2 = origin + ((u - hatchDx) * rightDir) + ((vGround - hatchDy) * upDir);

                    DetailCurve hatchLine = doc.Create.NewDetailCurve(view, Line.CreateBound(pt1, pt2));
                    TagGostElement(hatchLine, GostAnnotationComment);
                    if (thinStyle != null) try { hatchLine.LineStyle = thinStyle; } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Вычерчивает используемые координационные оси внизу разреза/фасада в кружках Ø8 мм
        /// с цепочкой шагов осей и общим габаритным размером между крайними осями по ГОСТ 21.501-93.
        /// </summary>
        private static void DrawGostBottomAxesAndDimensions(
            Document doc,
            ViewSection view,
            XYZ origin,
            XYZ rightDir,
            XYZ upDir,
            double uMin,
            double uMax,
            double groundZFt,
            double scaleFt,
            ElementId fontAxisId,
            ElementId fontDimId,
            GraphicsStyle? thinStyle)
        {
            try
            {
                double vGround = groundZFt - origin.Z;

                // Положение первой размерной цепочки (шаг осей): 10 мм ниже линии земли
                double vDimStep = vGround - (10.0 * scaleFt);
                // Положение второй размерной линии (общий размер): 16.5 мм ниже линии земли
                double vDimTotal = vGround - (16.5 * scaleFt);
                // Положение центров кружков осей: 25 мм ниже линии земли
                double vBubbles = vGround - (25.0 * scaleFt);
                // Радиус кружка оси: 4.0 мм на чертеже (Ø8 мм по ГОСТ 21.101-97)
                double radius = 4.0 * scaleFt;

                // Получаем только реально существующие используемые оси проекта, пересекающие вид
                List<GostGridItem> grids = GetProjectGridsForView(doc, view, origin, rightDir, uMin, uMax, scaleFt);
                if (grids.Count == 0) return;

                // 1. Для каждой оси вычерчиваем вертикальную линию, кружок Ø8 мм и марку
                foreach (GostGridItem g in grids)
                {
                    double u = g.U;

                    // Вертикальная линия оси: от земли до верха кружка
                    XYZ pTop = origin + (u * rightDir) + ((vGround - (3.5 * scaleFt)) * upDir);
                    XYZ pBot = origin + (u * rightDir) + ((vBubbles + radius) * upDir);

                    if (pTop.DistanceTo(pBot) > 0.005)
                    {
                        DetailCurve axisLine = doc.Create.NewDetailCurve(view, Line.CreateBound(pTop, pBot));
                        TagGostElement(axisLine, GostAnnotationComment);
                        if (thinStyle != null) try { axisLine.LineStyle = thinStyle; } catch { }
                    }

                    // Кружок оси Ø8 мм
                    XYZ centerPt = origin + (u * rightDir) + (vBubbles * upDir);
                    DrawCircle(doc, view, centerPt, radius, rightDir, upDir, thinStyle);

                    // Текст марки оси по центру кружка
                    XYZ pText = centerPt + ((1.75 * scaleFt) * upDir);
                    TextNoteOptions axisOpts = new TextNoteOptions
                    {
                        TypeId = fontAxisId,
                        HorizontalAlignment = HorizontalTextAlignment.Center
                    };

                    TextNote axisNote = TextNote.Create(doc, view.Id, pText, g.Name, axisOpts);
                    TagGostElement(axisNote, GostAnnotationComment);
                }

                if (grids.Count < 2) return;

                double uFirst = grids.First().U;
                double uLast = grids.Last().U;
                double tickLen = 2.5 * scaleFt;
                double tickD = tickLen * 0.5 * 0.7071;
                TextNoteOptions dimOpts = new TextNoteOptions
                {
                    TypeId = fontDimId,
                    HorizontalAlignment = HorizontalTextAlignment.Center
                };

                // 2. Линия 1: цепочка шагов между соседними осями
                XYZ pStepStart = origin + (uFirst * rightDir) + (vDimStep * upDir);
                XYZ pStepEnd = origin + (uLast * rightDir) + (vDimStep * upDir);

                if (pStepStart.DistanceTo(pStepEnd) > 0.05)
                {
                    DetailCurve stepCurve = doc.Create.NewDetailCurve(view, Line.CreateBound(pStepStart, pStepEnd));
                    TagGostElement(stepCurve, GostAnnotationComment);
                    if (thinStyle != null) try { stepCurve.LineStyle = thinStyle; } catch { }

                    for (int i = 0; i < grids.Count; i++)
                    {
                        double u = grids[i].U;
                        XYZ t1 = origin + ((u - tickD) * rightDir) + ((vDimStep - tickD) * upDir);
                        XYZ t2 = origin + ((u + tickD) * rightDir) + ((vDimStep + tickD) * upDir);
                        DetailCurve tick = doc.Create.NewDetailCurve(view, Line.CreateBound(t1, t2));
                        TagGostElement(tick, GostAnnotationComment);
                        if (thinStyle != null) try { tick.LineStyle = thinStyle; } catch { }

                        if (i < grids.Count - 1)
                        {
                            double uNext = grids[i + 1].U;
                            int stepMm = (int)Math.Round(((uNext - u) / MmToFeet) / 10.0) * 10;
                            if (stepMm > 0)
                            {
                                string stepText = stepMm.ToString(CultureInfo.InvariantCulture);
                                XYZ pStepText = origin + (((u + uNext) * 0.5) * rightDir) + ((vDimStep + (3.0 * scaleFt)) * upDir);
                                TextNote stepNote = TextNote.Create(doc, view.Id, pStepText, stepText, dimOpts);
                                TagGostElement(stepNote, GostAnnotationComment);
                            }
                        }
                    }
                }

                // 3. Линия 2: общий габаритный размер между крайними осями (если осей > 2)
                if (grids.Count > 2)
                {
                    XYZ pTotStart = origin + (uFirst * rightDir) + (vDimTotal * upDir);
                    XYZ pTotEnd = origin + (uLast * rightDir) + (vDimTotal * upDir);

                    if (pTotStart.DistanceTo(pTotEnd) > 0.05)
                    {
                        DetailCurve totCurve = doc.Create.NewDetailCurve(view, Line.CreateBound(pTotStart, pTotEnd));
                        TagGostElement(totCurve, GostAnnotationComment);
                        if (thinStyle != null) try { totCurve.LineStyle = thinStyle; } catch { }

                        foreach (double uEndPt in new[] { uFirst, uLast })
                        {
                            XYZ t1 = origin + ((uEndPt - tickD) * rightDir) + ((vDimTotal - tickD) * upDir);
                            XYZ t2 = origin + ((uEndPt + tickD) * rightDir) + ((vDimTotal + tickD) * upDir);
                            DetailCurve tick = doc.Create.NewDetailCurve(view, Line.CreateBound(t1, t2));
                            TagGostElement(tick, GostAnnotationComment);
                            if (thinStyle != null) try { tick.LineStyle = thinStyle; } catch { }
                        }

                        int totalDimMm = (int)Math.Round(((uLast - uFirst) / MmToFeet) / 10.0) * 10;
                        string dimText = totalDimMm.ToString(CultureInfo.InvariantCulture);

                        XYZ pDimText = origin + (((uFirst + uLast) * 0.5) * rightDir) + ((vDimTotal + (3.0 * scaleFt)) * upDir);
                        TextNote dimNote = TextNote.Create(doc, view.Id, pDimText, dimText, dimOpts);
                        TagGostElement(dimNote, GostAnnotationComment);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Вычерчивает высотные отметки строго в одну строчку (вертикальную цепочку) вдоль здания:
        /// Слева — полная цепочка высот (земля, чистый пол, подоконники, перемычки, этажи, карниз).
        /// Справа — характерные уровни здания в ровную строчку.
        /// </summary>
        private static void DrawDualGostElevationMarks(
            Document doc,
            ViewSection view,
            XYZ origin,
            XYZ rightDir,
            XYZ upDir,
            double uMin,
            double uMax,
            List<GostElevationItem> markItems,
            double scaleFt,
            ElementId fontMarkId,
            GraphicsStyle? thinStyle)
        {
            double d = 2.2 * scaleFt;          // подъем полочки и полуширина стрелки
            double shelfLen = 14.0 * scaleFt;  // длина полочки
            double offsetFromWall = 16.0 * scaleFt;

            // 1. Отметки СЛЕВА от здания (строго в одну вертикальную строчку)
            double uLeftTip = uMin - offsetFromWall;

            foreach (GostElevationItem item in markItems)
            {
                try
                {
                    double v = item.ElevationFt - origin.Z;
                    XYZ pTip = origin + (uLeftTip * rightDir) + (v * upDir);
                    XYZ pWall = origin + (uMin * rightDir) + (v * upDir);

                    // Выносная линия от стены
                    if (pTip.DistanceTo(pWall) > 0.005)
                    {
                        DetailCurve leader = doc.Create.NewDetailCurve(view, Line.CreateBound(pTip, pWall));
                        TagGostElement(leader, GostAnnotationComment);
                        if (thinStyle != null) try { leader.LineStyle = thinStyle; } catch { }
                    }

                    // Стрелка под 45°
                    XYZ pWingLeft = pTip - (d * rightDir) + (d * upDir);
                    XYZ pWingRight = pTip + (d * rightDir) + (d * upDir);
                    XYZ pShelfEnd = pTip - (shelfLen * rightDir) + (d * upDir);

                    DetailCurve w1 = doc.Create.NewDetailCurve(view, Line.CreateBound(pTip, pWingLeft));
                    DetailCurve w2 = doc.Create.NewDetailCurve(view, Line.CreateBound(pTip, pWingRight));
                    DetailCurve shelf = doc.Create.NewDetailCurve(view, Line.CreateBound(pWingRight, pShelfEnd));

                    TagGostElement(w1, GostAnnotationComment);
                    TagGostElement(w2, GostAnnotationComment);
                    TagGostElement(shelf, GostAnnotationComment);

                    if (thinStyle != null)
                    {
                        try { w1.LineStyle = thinStyle; } catch { }
                        try { w2.LineStyle = thinStyle; } catch { }
                        try { shelf.LineStyle = thinStyle; } catch { }
                    }

                    // Числовое значение над полочкой
                    XYZ pText = origin + ((uLeftTip - shelfLen + (0.5 * scaleFt)) * rightDir) + ((v + d + (3.0 * scaleFt)) * upDir);
                    TextNoteOptions noteOpts = new TextNoteOptions
                    {
                        TypeId = fontMarkId,
                        HorizontalAlignment = HorizontalTextAlignment.Left
                    };

                    TextNote elevNote = TextNote.Create(doc, view.Id, pText, item.FormattedMeters, noteOpts);
                    TagGostElement(elevNote, GostAnnotationComment);
                }
                catch { }
            }

            // 2. Отметки СПРАВА от здания (характерные высоты здания строго в одну строчку)
            // На архитектурном разрезе правые внешние высотные отметки исключаются, так как справа
            // на листе размещается таблица «Ведомость составов конструкций», а все высоты здания
            // уже нанесены слева в строчку и внутри разреза по плитам перекрытий!
            if (view.ViewType == ViewType.Section)
            {
                return;
            }

            double uRightTip = uMax + offsetFromWall;
            List<GostElevationItem> rightItems = markItems
                .Where(m => m.Priority >= 70)
                .ToList();

            foreach (GostElevationItem item in rightItems)
            {
                try
                {
                    double v = item.ElevationFt - origin.Z;
                    XYZ pTip = origin + (uRightTip * rightDir) + (v * upDir);
                    XYZ pWall = origin + (uMax * rightDir) + (v * upDir);

                    if (pTip.DistanceTo(pWall) > 0.005)
                    {
                        DetailCurve leader = doc.Create.NewDetailCurve(view, Line.CreateBound(pTip, pWall));
                        TagGostElement(leader, GostAnnotationComment);
                        if (thinStyle != null) try { leader.LineStyle = thinStyle; } catch { }
                    }

                    // Стрелка под 45° и полочка вправо
                    XYZ pWingLeft = pTip - (d * rightDir) + (d * upDir);
                    XYZ pWingRight = pTip + (d * rightDir) + (d * upDir);
                    XYZ pShelfEnd = pTip + (shelfLen * rightDir) + (d * upDir);

                    DetailCurve w1 = doc.Create.NewDetailCurve(view, Line.CreateBound(pTip, pWingLeft));
                    DetailCurve w2 = doc.Create.NewDetailCurve(view, Line.CreateBound(pTip, pWingRight));
                    DetailCurve shelf = doc.Create.NewDetailCurve(view, Line.CreateBound(pWingLeft, pShelfEnd));

                    TagGostElement(w1, GostAnnotationComment);
                    TagGostElement(w2, GostAnnotationComment);
                    TagGostElement(shelf, GostAnnotationComment);

                    if (thinStyle != null)
                    {
                        try { w1.LineStyle = thinStyle; } catch { }
                        try { w2.LineStyle = thinStyle; } catch { }
                        try { shelf.LineStyle = thinStyle; } catch { }
                    }

                    // Текст над правой полочкой
                    XYZ pText = origin + ((uRightTip + d + (0.8 * scaleFt)) * rightDir) + ((v + d + (3.0 * scaleFt)) * upDir);
                    TextNoteOptions noteOpts = new TextNoteOptions
                    {
                        TypeId = fontMarkId,
                        HorizontalAlignment = HorizontalTextAlignment.Left
                    };

                    TextNote elevNote = TextNote.Create(doc, view.Id, pText, item.FormattedMeters, noteOpts);
                    TagGostElement(elevNote, GostAnnotationComment);
                }
                catch { }
            }
        }

        /// <summary>
        /// Вычерчивает высотные отметки конька кровли и трубы в верхней части чертежа.
        /// </summary>
        private static void DrawRoofRidgeAndChimneyMarks(
            Document doc,
            ViewSection view,
            XYZ origin,
            XYZ rightDir,
            XYZ upDir,
            double uMin,
            double uMax,
            double zBuildingMax,
            double scaleFt,
            ElementId fontMarkId,
            GraphicsStyle? thinStyle)
        {
            try
            {
                double d = 2.2 * scaleFt;
                double shelfLen = 12.0 * scaleFt;
                double vRoof = zBuildingMax - origin.Z;

                // 1. Отметка конька кровли по центру
                double uRidge = uMin + ((uMax - uMin) * 0.48);
                XYZ pRidgeTip = origin + (uRidge * rightDir) + (vRoof * upDir);

                // Стрелка направлена вниз к вершине конька
                XYZ rWingLeft = pRidgeTip - (d * rightDir) + (d * upDir);
                XYZ rWingRight = pRidgeTip + (d * rightDir) + (d * upDir);
                XYZ rLeaderTop = pRidgeTip + ((10.0 * scaleFt) * upDir);
                XYZ rShelfEnd = rLeaderTop - (shelfLen * rightDir);

                DetailCurve rw1 = doc.Create.NewDetailCurve(view, Line.CreateBound(pRidgeTip, rWingLeft));
                DetailCurve rw2 = doc.Create.NewDetailCurve(view, Line.CreateBound(pRidgeTip, rWingRight));
                DetailCurve rLeader = doc.Create.NewDetailCurve(view, Line.CreateBound(pRidgeTip, rLeaderTop));
                DetailCurve rShelf = doc.Create.NewDetailCurve(view, Line.CreateBound(rLeaderTop, rShelfEnd));

                TagGostElement(rw1, GostAnnotationComment);
                TagGostElement(rw2, GostAnnotationComment);
                TagGostElement(rLeader, GostAnnotationComment);
                TagGostElement(rShelf, GostAnnotationComment);

                if (thinStyle != null)
                {
                    try { rw1.LineStyle = thinStyle; } catch { }
                    try { rw2.LineStyle = thinStyle; } catch { }
                    try { rLeader.LineStyle = thinStyle; } catch { }
                    try { rShelf.LineStyle = thinStyle; } catch { }
                }

                // Текст отметки конька (например 8,668)
                double ridgeM = zBuildingMax * 0.3048;
                string ridgeText = FormatGostElevation(ridgeM);
                XYZ pRidgeText = origin + ((uRidge - shelfLen) * rightDir) + ((vRoof + (10.0 * scaleFt) + (3.0 * scaleFt)) * upDir);

                TextNoteOptions noteOpts = new TextNoteOptions
                {
                    TypeId = fontMarkId,
                    HorizontalAlignment = HorizontalTextAlignment.Left
                };

                TextNote ridgeNote = TextNote.Create(doc, view.Id, pRidgeText, ridgeText, noteOpts);
                TagGostElement(ridgeNote, GostAnnotationComment);

                // 2. Вторая отметка (труба / парапет) справа от конька (если габарит здания позволяет)
                if (uMax - uMin > 30.0 * MmToFeet * 1000)
                {
                    double uChimney = uMin + ((uMax - uMin) * 0.76);
                    double vChimney = vRoof - (0.8 / 0.3048); // чуть ниже конька
                    XYZ pChimneyTip = origin + (uChimney * rightDir) + (vChimney * upDir);

                    XYZ cWingLeft = pChimneyTip - (d * rightDir) + (d * upDir);
                    XYZ cWingRight = pChimneyTip + (d * rightDir) + (d * upDir);
                    XYZ cLeaderTop = pChimneyTip + ((8.0 * scaleFt) * upDir);
                    XYZ cShelfEnd = cLeaderTop + (shelfLen * rightDir);

                    DetailCurve cw1 = doc.Create.NewDetailCurve(view, Line.CreateBound(pChimneyTip, cWingLeft));
                    DetailCurve cw2 = doc.Create.NewDetailCurve(view, Line.CreateBound(pChimneyTip, cWingRight));
                    DetailCurve cLeader = doc.Create.NewDetailCurve(view, Line.CreateBound(pChimneyTip, cLeaderTop));
                    DetailCurve cShelf = doc.Create.NewDetailCurve(view, Line.CreateBound(cLeaderTop, cShelfEnd));

                    TagGostElement(cw1, GostAnnotationComment);
                    TagGostElement(cw2, GostAnnotationComment);
                    TagGostElement(cLeader, GostAnnotationComment);
                    TagGostElement(cShelf, GostAnnotationComment);

                    if (thinStyle != null)
                    {
                        try { cw1.LineStyle = thinStyle; } catch { }
                        try { cw2.LineStyle = thinStyle; } catch { }
                        try { cLeader.LineStyle = thinStyle; } catch { }
                        try { cShelf.LineStyle = thinStyle; } catch { }
                    }

                    string chimneyText = FormatGostElevation((zBuildingMax - (0.8 / 0.3048)) * 0.3048);
                    XYZ pChimneyText = origin + ((uChimney + (2.0 * scaleFt)) * rightDir) + ((vChimney + (8.0 * scaleFt) + (3.0 * scaleFt)) * upDir);
                    TextNote chimneyNote = TextNote.Create(doc, view.Id, pChimneyText, chimneyText, noteOpts);
                    TagGostElement(chimneyNote, GostAnnotationComment);
                }
            }
            catch { }
        }

        /// <summary>
        /// Специфика архитектурных разрезов: внутренние отметки перекрытий и выноски многослойных конструкций.
        /// </summary>
        private static void DrawSectionSpecificInteriorAnnotations(
            Document doc,
            ViewSection view,
            XYZ origin,
            XYZ rightDir,
            XYZ upDir,
            double uMin,
            double uMax,
            double groundZFt,
            double zBuildingMax,
            List<GostElevationItem> markItems,
            double scaleFt,
            ElementId fontMarkId,
            ElementId fontLayerId,
            GraphicsStyle? thinStyle)
        {
            try
            {
                double d = 2.0 * scaleFt;
                double shelfLen = 10.0 * scaleFt;

                // 1. Внутренние высотные отметки на плитах перекрытий (±0.000, +3.300, -0.200)
                double uInteriorMark = uMin + ((uMax - uMin) * 0.42);

                foreach (GostElevationItem item in markItems)
                {
                    // Пропускаем верх кровли (он уже оформлен коньком)
                    if (Math.Abs(item.ElevationFt - zBuildingMax) < 1.0) continue;

                    double v = item.ElevationFt - origin.Z;
                    XYZ pSlabTip = origin + (uInteriorMark * rightDir) + (v * upDir);

                    // Стрелка опирается острием прямо на чистый пол / перекрытие
                    XYZ w1 = pSlabTip - (d * rightDir) + (d * upDir);
                    XYZ w2 = pSlabTip + (d * rightDir) + (d * upDir);
                    XYZ stemTop = pSlabTip + ((2.2 * scaleFt) * upDir);
                    XYZ shelfEnd = stemTop + (shelfLen * rightDir);

                    DetailCurve cw1 = doc.Create.NewDetailCurve(view, Line.CreateBound(pSlabTip, w1));
                    DetailCurve cw2 = doc.Create.NewDetailCurve(view, Line.CreateBound(pSlabTip, w2));
                    DetailCurve stem = doc.Create.NewDetailCurve(view, Line.CreateBound(pSlabTip, stemTop));
                    DetailCurve shelf = doc.Create.NewDetailCurve(view, Line.CreateBound(stemTop, shelfEnd));

                    TagGostElement(cw1, GostAnnotationComment);
                    TagGostElement(cw2, GostAnnotationComment);
                    TagGostElement(stem, GostAnnotationComment);
                    TagGostElement(shelf, GostAnnotationComment);

                    if (thinStyle != null)
                    {
                        try { cw1.LineStyle = thinStyle; } catch { }
                        try { cw2.LineStyle = thinStyle; } catch { }
                        try { stem.LineStyle = thinStyle; } catch { }
                        try { shelf.LineStyle = thinStyle; } catch { }
                    }

                    // Текст отметки: ±0.000 или число
                    string textVal = Math.Abs(item.ElevationFt * 0.3048) < 0.05 ? "±0,000" : item.FormattedMeters;
                    XYZ pText = origin + ((uInteriorMark + (1.5 * scaleFt)) * rightDir) + ((v + (2.2 * scaleFt) + (2.8 * scaleFt)) * upDir);

                    TextNoteOptions noteOpts = new TextNoteOptions
                    {
                        TypeId = fontMarkId,
                        HorizontalAlignment = HorizontalTextAlignment.Left
                    };

                    TextNote note = TextNote.Create(doc, view.Id, pText, textVal, noteOpts);
                    TagGostElement(note, GostAnnotationComment);
                }

                // 2. Выносная «этажерка» пирога совмещенной кровли (ГОСТ 21.501-93)
                double uRoofFlag = uMin + ((uMax - uMin) * 0.58);
                double vRoofFlag = (zBuildingMax - origin.Z) + (3.0 * scaleFt);
                XYZ roofLeaderStart = origin + (uRoofFlag * rightDir) + ((zBuildingMax - origin.Z) * upDir);

                (string Name, string Thickness)[] roofLayers =
                [
                    ("Рулонная кровельная мембрана", "– 8 мм"),
                    ("Гидроизоляционная мембрана", "– 3 мм"),
                    ("Цементно-песчаная стяжка", "– 40 мм"),
                    ("Керамзитовый гравий (создание уклона)", "– 40 мм"),
                    ("Полиэтиленовая изоляционная мембрана", "– 1 мм"),
                    ("Теплоизоляция (минеральная вата, слой 1)", "– 100 мм"),
                    ("Теплоизоляция (минеральная вата, слой 2)", "– 140 мм"),
                    ("Пароизоляционная мембрана", "– 2 мм"),
                    ("Бетонная плита покрытия", "– 200 мм")
                ];

                DrawGostFlagCallout(doc, view, origin, rightDir, upDir,
                    uLeft: uRoofFlag, vBottom: vRoofFlag,
                    leaderStartPoint: roofLeaderStart,
                    layers: roofLayers,
                    scaleFt: scaleFt,
                    fontLayerId: fontLayerId,
                    thinStyle: thinStyle,
                    arrowPointingUp: false);

                // 3. Выносная «этажерка» пирога пола 1-го этажа / перекрытия
                double floorElevationFt = markItems.FirstOrDefault(m => Math.Abs(m.ElevationFt * 0.3048) < 0.1)?.ElevationFt ?? 0.0;
                double uFloorFlag = uMin + ((uMax - uMin) * 0.16);
                double vFloorSlab = floorElevationFt - origin.Z;
                double vFloorFlag = vFloorSlab + (16.0 * scaleFt);
                XYZ floorLeaderStart = origin + (uFloorFlag * rightDir) + (vFloorSlab * upDir);

                (string Name, string Thickness)[] floorLayers =
                [
                    ("Покрытие пола (керамогранитная плитка)", "– 15 мм"),
                    ("Клеевой состав для плитки", "– 5 мм"),
                    ("Цементно-песчаная стяжка армированная", "– 40 мм"),
                    ("Звукоизоляционная мембрана", "– 5 мм"),
                    ("Теплоизоляция (экструдированный пенополистирол)", "– 50 мм"),
                    ("Монолитная железобетонная плита", "– 200 мм"),
                    ("Штукатурка потолочная / шпатлевка", "– 5 мм")
                ];

                DrawGostFlagCallout(doc, view, origin, rightDir, upDir,
                    uLeft: uFloorFlag, vBottom: vFloorFlag,
                    leaderStartPoint: floorLeaderStart,
                    layers: floorLayers,
                    scaleFt: scaleFt,
                    fontLayerId: fontLayerId,
                    thinStyle: thinStyle,
                    arrowPointingUp: true);
            }
            catch { }
        }

        /// <summary>
        /// Вычерчивает нормативную ГОСТ-выноску пирога конструкции («флажок» / табличка слоев) с выноской и стрелкой к конструкции.
        /// Каждая строка содержит наименование слоя слева и толщину со знаком минус справа, подчеркнутые горизонтальной линией.
        /// </summary>
        private static void DrawGostFlagCallout(
            Document doc,
            ViewSection view,
            XYZ origin,
            XYZ rightDir,
            XYZ upDir,
            double uLeft,
            double vBottom,
            XYZ leaderStartPoint,
            (string Name, string Thickness)[] layers,
            double scaleFt,
            ElementId fontLayerId,
            GraphicsStyle? thinStyle,
            bool arrowPointingUp)
        {
            try
            {
                if (layers == null || layers.Length == 0) return;

                double totalWidthMm = 82.0; // 82 мм ширина флажка для свободного размещения длинных наименований
                double rowHeightMm = 4.4;   // 4.4 мм высота строки
                double totalWidthFt = totalWidthMm * scaleFt;
                double rowHeightFt = rowHeightMm * scaleFt;

                int rowCount = layers.Length;
                double totalHeightFt = rowCount * rowHeightFt;

                double vStart = vBottom + totalHeightFt;

                // Вертикальная стойка флажка (линия слева)
                // Если стрелка направлена вверх (для пола): вертикальная стойка продолжается вверх на 3.5 мм над верхней полочкой
                // и оканчивается острой стрелкой вверх, указывающей на конструкцию/вверх
                // Если стрелка вниз (для кровли): вертикальная стойка опускается к конструкции leaderStartPoint
                double arrowExtMm = 3.8;
                double arrowExtFt = arrowExtMm * scaleFt;

                XYZ pColTop = origin + (uLeft * rightDir) + ((vStart + (arrowPointingUp ? arrowExtFt : 0.0)) * upDir);
                XYZ pColBot = origin + (uLeft * rightDir) + (vBottom * upDir);

                // Отрисовка стрелки на вертикальной линии:
                if (arrowPointingUp)
                {
                    // Вертикальная линия со стрелкой вверх на верхнем конце
                    DetailCurve colLine = doc.Create.NewDetailCurve(view, Line.CreateBound(pColBot, pColTop));
                    TagGostElement(colLine, GostAnnotationComment);
                    if (thinStyle != null) try { colLine.LineStyle = thinStyle; } catch { }

                    // Стрелка вверх на pColTop
                    double aLen = 2.0 * scaleFt;
                    double aWidth = 0.55 * scaleFt;
                    XYZ aLeft = pColTop - (aLen * upDir) - (aWidth * rightDir);
                    XYZ aRight = pColTop - (aLen * upDir) + (aWidth * rightDir);
                    DetailCurve arrow1 = doc.Create.NewDetailCurve(view, Line.CreateBound(pColTop, aLeft));
                    DetailCurve arrow2 = doc.Create.NewDetailCurve(view, Line.CreateBound(pColTop, aRight));
                    TagGostElement(arrow1, GostAnnotationComment);
                    TagGostElement(arrow2, GostAnnotationComment);
                    if (thinStyle != null)
                    {
                        try { arrow1.LineStyle = thinStyle; } catch { }
                        try { arrow2.LineStyle = thinStyle; } catch { }
                    }
                }
                else
                {
                    // Кровля: вертикальная линия от верхней полочки вниз до leaderStartPoint
                    XYZ pRoofLeaderEnd = leaderStartPoint;
                    DetailCurve colLine = doc.Create.NewDetailCurve(view, Line.CreateBound(pRoofLeaderEnd, pColTop));
                    TagGostElement(colLine, GostAnnotationComment);
                    if (thinStyle != null) try { colLine.LineStyle = thinStyle; } catch { }

                    // Стрелка вниз на leaderStartPoint
                    double aLen = 2.0 * scaleFt;
                    double aWidth = 0.55 * scaleFt;
                    XYZ aLeft = pRoofLeaderEnd + (aLen * upDir) - (aWidth * rightDir);
                    XYZ aRight = pRoofLeaderEnd + (aLen * upDir) + (aWidth * rightDir);
                    DetailCurve arrow1 = doc.Create.NewDetailCurve(view, Line.CreateBound(pRoofLeaderEnd, aLeft));
                    DetailCurve arrow2 = doc.Create.NewDetailCurve(view, Line.CreateBound(pRoofLeaderEnd, aRight));
                    TagGostElement(arrow1, GostAnnotationComment);
                    TagGostElement(arrow2, GostAnnotationComment);
                    if (thinStyle != null)
                    {
                        try { arrow1.LineStyle = thinStyle; } catch { }
                        try { arrow2.LineStyle = thinStyle; } catch { }
                    }
                }

                double curV = vStart;

                // Верхняя горизонтальная полочка флажка
                XYZ pTopL = origin + (uLeft * rightDir) + (curV * upDir);
                XYZ pTopR = origin + ((uLeft + totalWidthFt) * rightDir) + (curV * upDir);
                DetailCurve topLine = doc.Create.NewDetailCurve(view, Line.CreateBound(pTopL, pTopR));
                TagGostElement(topLine, GostAnnotationComment);
                if (thinStyle != null) try { topLine.LineStyle = thinStyle; } catch { }

                TextNoteOptions leftOpts = new TextNoteOptions
                {
                    TypeId = fontLayerId,
                    HorizontalAlignment = HorizontalTextAlignment.Left
                };

                for (int i = 0; i < rowCount; i++)
                {
                    double nextV = curV - rowHeightFt;

                    // Горизонтальная линия под строкой
                    XYZ pShelfL = origin + (uLeft * rightDir) + (nextV * upDir);
                    XYZ pShelfR = origin + ((uLeft + totalWidthFt) * rightDir) + (nextV * upDir);
                    DetailCurve shelf = doc.Create.NewDetailCurve(view, Line.CreateBound(pShelfL, pShelfR));
                    TagGostElement(shelf, GostAnnotationComment);
                    if (thinStyle != null) try { shelf.LineStyle = thinStyle; } catch { }

                    // Текст наименования слоя (слева)
                    XYZ pTextL = origin + ((uLeft + (1.2 * scaleFt)) * rightDir) + ((nextV + (3.4 * scaleFt)) * upDir);
                    TextNote nameNote = TextNote.Create(doc, view.Id, pTextL, layers[i].Name, leftOpts);
                    TagGostElement(nameNote, GostAnnotationComment);

                    // Текст толщины слоя со знаком минус (выравнивание слева от фиксированной позиции 62 мм)
                    XYZ pTextR = origin + ((uLeft + (61.0 * scaleFt)) * rightDir) + ((nextV + (3.4 * scaleFt)) * upDir);
                    TextNote thkNote = TextNote.Create(doc, view.Id, pTextR, layers[i].Thickness, leftOpts);
                    TagGostElement(thkNote, GostAnnotationComment);

                    curV = nextV;
                }
            }
            catch { }
        }

        /// <summary>
        /// Вычерчивает классическую «этажерку» пирога конструкции по ГОСТ 21.501-93 Форма 4.
        /// </summary>
        private static void DrawMultilayerCalloutStack(
            Document doc,
            ViewSection view,
            XYZ origin,
            XYZ rightDir,
            XYZ upDir,
            double uLeft,
            double vTop,
            XYZ leaderStartPoint,
            string[] layers,
            string? totalThkText,
            double scaleFt,
            ElementId fontLayerId,
            GraphicsStyle? thinStyle)
        {
            try
            {
                double shelfWidth = 52.0 * scaleFt;
                double rowHeight = 2.9 * scaleFt;

                // Линия-выноска от конструкции к этажерке
                XYZ calloutBasePt = origin + (uLeft * rightDir) + (vTop * upDir);
                if (leaderStartPoint.DistanceTo(calloutBasePt) > 0.005)
                {
                    DetailCurve leaderLine = doc.Create.NewDetailCurve(view, Line.CreateBound(leaderStartPoint, calloutBasePt));
                    TagGostElement(leaderLine, GostAnnotationComment);
                    if (thinStyle != null) try { leaderLine.LineStyle = thinStyle; } catch { }
                }

                double curV = vTop;

                // Полочки и текстовые строки слоев
                for (int i = 0; i < layers.Length; i++)
                {
                    XYZ pShelfStart = origin + (uLeft * rightDir) + (curV * upDir);
                    XYZ pShelfEnd = origin + ((uLeft + shelfWidth) * rightDir) + (curV * upDir);

                    DetailCurve shelf = doc.Create.NewDetailCurve(view, Line.CreateBound(pShelfStart, pShelfEnd));
                    TagGostElement(shelf, GostAnnotationComment);
                    if (thinStyle != null) try { shelf.LineStyle = thinStyle; } catch { }

                    // Текст слоя над полочкой
                    XYZ pText = origin + ((uLeft + (1.2 * scaleFt)) * rightDir) + ((curV + (2.2 * scaleFt)) * upDir);
                    TextNoteOptions noteOpts = new TextNoteOptions
                    {
                        TypeId = fontLayerId,
                        HorizontalAlignment = HorizontalTextAlignment.Left
                    };

                    TextNote layerNote = TextNote.Create(doc, view.Id, pText, layers[i], noteOpts);
                    TagGostElement(layerNote, GostAnnotationComment);

                    curV -= rowHeight;
                }

                // Замыкающая нижняя полочка
                XYZ pBotStart = origin + (uLeft * rightDir) + (curV * upDir);
                XYZ pBotEnd = origin + ((uLeft + shelfWidth) * rightDir) + (curV * upDir);
                DetailCurve botShelf = doc.Create.NewDetailCurve(view, Line.CreateBound(pBotStart, pBotEnd));
                TagGostElement(botShelf, GostAnnotationComment);
                if (thinStyle != null) try { botShelf.LineStyle = thinStyle; } catch { }

                // Вертикальная засечка толщины слева (если задана)
                if (!string.IsNullOrWhiteSpace(totalThkText))
                {
                    XYZ pBracketTop = origin + ((uLeft - (3.5 * scaleFt)) * rightDir) + (vTop * upDir);
                    XYZ pBracketBot = origin + ((uLeft - (3.5 * scaleFt)) * rightDir) + (curV * upDir);
                    DetailCurve bracket = doc.Create.NewDetailCurve(view, Line.CreateBound(pBracketTop, pBracketBot));
                    TagGostElement(bracket, GostAnnotationComment);
                    if (thinStyle != null) try { bracket.LineStyle = thinStyle; } catch { }

                    XYZ pThkText = origin + ((uLeft - (5.0 * scaleFt)) * rightDir) + (((vTop + curV) / 2.0 + (1.0 * scaleFt)) * upDir);
                    TextNoteOptions thkOpts = new TextNoteOptions
                    {
                        TypeId = fontLayerId,
                        HorizontalAlignment = HorizontalTextAlignment.Right
                    };
                    TextNote thkNote = TextNote.Create(doc, view.Id, pThkText, totalThkText, thkOpts);
                    TagGostElement(thkNote, GostAnnotationComment);
                }
            }
            catch { }
        }

        /// <summary>
        /// Вычерчивает замкнутый кружок через два полукруга в Revit API.
        /// </summary>
        private static void DrawCircle(
            Document doc,
            View view,
            XYZ centerPt,
            double radius,
            XYZ xAxis,
            XYZ yAxis,
            GraphicsStyle? style)
        {
            try
            {
                Arc arc1 = Arc.Create(centerPt, radius, 0.0, Math.PI, xAxis, yAxis);
                Arc arc2 = Arc.Create(centerPt, radius, Math.PI, 2.0 * Math.PI, xAxis, yAxis);

                DetailCurve c1 = doc.Create.NewDetailCurve(view, arc1);
                DetailCurve c2 = doc.Create.NewDetailCurve(view, arc2);

                TagGostElement(c1, GostAnnotationComment);
                TagGostElement(c2, GostAnnotationComment);

                if (style != null)
                {
                    try { c1.LineStyle = style; } catch { }
                    try { c2.LineStyle = style; } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Проецирует только реально существующие и используемые координационные оси здания на плоскость проекции вида.
        /// Не создает искусственных осей, если их нет в модели.
        /// </summary>
        private static List<GostGridItem> GetProjectGridsForView(
            Document doc,
            ViewSection view,
            XYZ origin,
            XYZ rightDir,
            double uMin,
            double uMax,
            double scaleFt)
        {
            return GostGridManager.GetUsedGridsForSectionOrElevation(doc, view, origin, rightDir, uMin, uMax, scaleFt);
        }

        /// <summary>
        /// Подгоняет CropBox вида так, чтобы здание, земля, строчка отметок и нижние кружки осей с размерами идеально поместились.
        /// </summary>
        private static void AdjustCropBoxToFitGostElements(
            ViewSection view,
            Transform t,
            XYZ origin,
            double uMin,
            double uMax,
            double groundZFt,
            double roofZFt,
            double scaleFt,
            bool isSection)
        {
            try
            {
                double vGround = groundZFt - origin.Z;
                double vRoof = roofZFt - origin.Z;

                // Запас слева (под строчку отметок высот: выноска 16 мм + полочка 14 мм + поля = 38 мм)
                double leftPadFt = 38.0 * scaleFt;
                // Запас справа: для разрезов правые отметки исключены (справа таблица ведомостей), поэтому запас минимальный (8 мм)
                double rightPadFt = (isSection ? 8.0 : 38.0) * scaleFt;
                // Запас снизу (под линию земли, две размерные линии и кружки осей Ø8 мм на отметке -25 мм: 33 мм)
                double bottomPadFt = 33.0 * scaleFt;
                // Запас сверху (под отметку конька и этажерку кровли: 45 мм)
                double topPadFt = (isSection ? 45.0 : 25.0) * scaleFt;

                BoundingBoxXYZ cropBox = new BoundingBoxXYZ
                {
                    Transform = t,
                    Min = new XYZ(uMin - leftPadFt, vGround - bottomPadFt, view.CropBox.Min.Z),
                    Max = new XYZ(uMax + rightPadFt, vRoof + topPadFt, view.CropBox.Max.Z)
                };

                view.CropBox = cropBox;
                view.CropBoxActive = true;
                view.CropBoxVisible = false;
            }
            catch { }
        }

        /// <summary>
        /// Собирает список характерных высотных отметок здания по ГОСТ (без учета генплана).
        /// </summary>
        private static List<GostElevationItem> CollectElevationItems(Document doc, double zMin, double zMax)
        {
            List<GostElevationItem> rawItems = [];

            // 1. Уровни проекта (Levels), исключая любые уровни генплана
            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Where(lvl =>
                {
                    string name = lvl.Name ?? string.Empty;
                    return !name.StartsWith("ГП", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("Генплан", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("Site", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("Топо", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("Земл", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(l => l.Elevation)
                .ToList();

            bool hasZeroLevel = false;
            foreach (Level lvl in levels)
            {
                double elevFt = lvl.Elevation;
                double elevM = elevFt * 0.3048;

                // Пропускаем уровни глубже -1.0 м (подземный котлован/фундамент), если это не чистый пол
                if (elevM < -1.0) continue;

                string label;
                int priority;

                if (Math.Abs(elevM) < 0.05)
                {
                    label = "Ур. ч. п.";
                    priority = 100;
                    hasZeroLevel = true;
                }
                else
                {
                    label = lvl.Name;
                    priority = 80;
                }

                rawItems.Add(new GostElevationItem(elevFt, FormatGostElevation(elevM), label, priority));
            }

            // Чистый пол 0.000 обязателен для архитектурного чертежа
            if (!hasZeroLevel)
            {
                rawItems.Add(new GostElevationItem(0.0, "0,000", "Ур. ч. п.", 100));
            }

            // 2. Отметка планировочной земли (отмостки здания)
            // По ГОСТ отмостка/уровень земли у входа обычно на -0.150 м (или от -0.100 до -0.300 м)
            // ИСКЛЮЧАЕМ любые отметки генплана (-1.6м, -3.2м и т.д.)
            double groundM = -0.150;
            try
            {
                var exteriorWalls = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .Where(w => !SheetLayoutEngine.IsSitePlanElement(doc, w))
                    .ToList();

                double minWallZ = double.MaxValue;
                foreach (var w in exteriorWalls)
                {
                    BoundingBoxXYZ? bb = w.get_BoundingBox(null);
                    if (bb != null && bb.Min.Z * 0.3048 >= -0.60 && bb.Min.Z * 0.3048 <= -0.05)
                    {
                        minWallZ = Math.Min(minWallZ, bb.Min.Z);
                    }
                }

                if (minWallZ < double.MaxValue)
                {
                    groundM = Math.Round(minWallZ * 0.3048, 2);
                }
            }
            catch { }

            double groundFt = groundM / 0.3048;
            rawItems.Add(new GostElevationItem(groundFt, FormatGostElevation(groundM), "Ур. земли", 95));

            // 3. Архитектурные отметки окон и дверей (низ и верх проемов)
            try
            {
                var openings = new FilteredElementCollector(doc)
                    .WherePasses(new LogicalOrFilter(
                        new ElementCategoryFilter(BuiltInCategory.OST_Windows),
                        new ElementCategoryFilter(BuiltInCategory.OST_Doors)))
                    .WhereElementIsNotElementType()
                    .Where(e => !SheetLayoutEngine.IsSitePlanElement(doc, e))
                    .ToList();

                List<double> sillHeightsM = [];
                List<double> headHeightsM = [];

                foreach (Element op in openings)
                {
                    BoundingBoxXYZ? bb = op.get_BoundingBox(null);
                    if (bb == null) continue;

                    double sillM = Math.Round(bb.Min.Z * 0.3048, 2);
                    double headM = Math.Round(bb.Max.Z * 0.3048, 2);

                    if (sillM > 0.30 && sillM < (zMax * 0.3048 - 1.0))
                    {
                        sillHeightsM.Add(sillM);
                    }
                    if (headM > 1.80 && headM < (zMax * 0.3048 - 0.5))
                    {
                        headHeightsM.Add(headM);
                    }
                }

                var topSills = sillHeightsM
                    .GroupBy(h => Math.Round(h, 1))
                    .OrderByDescending(g => g.Count())
                    .Take(3)
                    .Select(g => g.Key);

                foreach (double sM in topSills)
                {
                    rawItems.Add(new GostElevationItem(sM / 0.3048, FormatGostElevation(sM), "Низ проема", 65));
                }

                var topHeads = headHeightsM
                    .GroupBy(h => Math.Round(h, 1))
                    .OrderByDescending(g => g.Count())
                    .Take(2)
                    .Select(g => g.Key);

                foreach (double hM in topHeads)
                {
                    rawItems.Add(new GostElevationItem(hM / 0.3048, FormatGostElevation(hM), "Верх проема", 65));
                }
            }
            catch { }

            // 4. Отметка карниза (верх наружных стен здания)
            try
            {
                var walls = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .Where(w => !SheetLayoutEngine.IsSitePlanElement(doc, w))
                    .ToList();

                double maxWallZ = double.MinValue;
                foreach (var w in walls)
                {
                    BoundingBoxXYZ? bb = w.get_BoundingBox(null);
                    if (bb != null && bb.Max.Z > maxWallZ)
                    {
                        maxWallZ = bb.Max.Z;
                    }
                }

                if (maxWallZ > 0)
                {
                    double corniceM = Math.Round(maxWallZ * 0.3048, 3);
                    rawItems.Add(new GostElevationItem(maxWallZ, FormatGostElevation(corniceM), "Низ карниза", 75));
                }
            }
            catch { }

            // 5. Отметка парапета / конька кровли (по чистому габариту здания)
            double roofM = Math.Round(zMax * 0.3048, 3);
            rawItems.Add(new GostElevationItem(zMax, FormatGostElevation(roofM), "Конек", 85));

            // 6. Фильтрация слишком близких отметок (< 350 мм друг от друга)
            return FilterOverlappingElevations(rawItems);
        }

        /// <summary>
        /// Устраняет наложение близких отметок (оставляет более приоритетные).
        /// </summary>
        private static List<GostElevationItem> FilterOverlappingElevations(List<GostElevationItem> items)
        {
            var sorted = items.OrderBy(i => i.ElevationFt).ToList();
            List<GostElevationItem> filtered = [];

            const double minDistanceFt = 1.0; // ~300 мм

            foreach (GostElevationItem item in sorted)
            {
                if (filtered.Count == 0)
                {
                    filtered.Add(item);
                    continue;
                }

                GostElevationItem last = filtered[^1];
                if (Math.Abs(item.ElevationFt - last.ElevationFt) < minDistanceFt)
                {
                    if (item.Priority > last.Priority)
                    {
                        filtered[^1] = item;
                    }
                }
                else
                {
                    filtered.Add(item);
                }
            }

            return filtered;
        }

        /// <summary>
        /// Удаляет ранее построенные плагином высотные отметки, оси и линию земли.
        /// </summary>
        private static void CleanPreviousGostAnnotations(Document doc, View view)
        {
            try
            {
                List<ElementId> toDelete = [];

                var curves = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(CurveElement))
                    .Where(c =>
                    {
                        string? cmt = c.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                        return cmt is GostAnnotationComment or GostGroundComment;
                    })
                    .Select(c => c.Id);
                toDelete.AddRange(curves);

                var notes = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(TextNote))
                    .Where(t =>
                    {
                        string? cmt = t.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                        return cmt == GostAnnotationComment;
                    })
                    .Select(t => t.Id);
                toDelete.AddRange(notes);

                if (toDelete.Count > 0)
                {
                    doc.Delete(toDelete);
                }
            }
            catch { }
        }

        private static void TagGostElement(Element element, string tag)
        {
            try
            {
                Parameter? param = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (param is { IsReadOnly: false })
                {
                    param.Set(tag);
                }
            }
            catch { }
        }

        /// <summary>
        /// Форматирует отметку в метрах с тремя знаками по ГОСТ (0,000, +3,300, -0,900).
        /// </summary>
        public static string FormatGostElevation(double meters)
        {
            if (Math.Abs(meters) < 0.001)
            {
                return "0,000";
            }

            if (meters > 0)
            {
                return string.Format(CultureInfo.InvariantCulture, "+{0:F3}", meters).Replace('.', ',');
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:F3}", meters).Replace('.', ',');
        }

        /// <summary>
        /// Создает или находит стандартный текстовый тип по ГОСТ (ISOCPEUR).
        /// </summary>
        private static ElementId GetOrCreateGostTextType(Document doc, double heightMm, bool bold, string suffix)
        {
            string typeName = $"АР_ГОСТ_{suffix}_{heightMm:F1}мм";
            TextNoteType? existing = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

            if (existing != null) return existing.Id;

            TextNoteType? baseType = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault();

            if (baseType == null) return ElementId.InvalidElementId;

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

        private static GraphicsStyle? GetThinLineStyle(Document doc)
        {
            try
            {
                Category linesCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                foreach (Category subCat in linesCat.SubCategories)
                {
                    if (subCat.Name.Contains("Тонк", StringComparison.OrdinalIgnoreCase) ||
                        subCat.Name.Contains("Thin", StringComparison.OrdinalIgnoreCase))
                    {
                        return subCat.GetGraphicsStyle(GraphicsStyleType.Projection);
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
