using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitViewPlacer
{
    /// <summary>
    /// Автоматическое приведение координационных осей в проекте к стандарту ГОСТ 21.101-97 / ГОСТ 21.501-93:
    /// 1. Переименование существующих осей (без создания новых осей в модели):
    ///    - Используемые вертикальные оси: цифры (1, 2, 3...) слева направо (по оси X)
    ///    - Используемые горизонтальные оси: буквы (А, Б, В... без Ё, З, Й, О, Х, Ц, Ч, Щ, Ъ, Ы, Ь) снизу вверх (по оси Y)
    ///    - Растягивание 3D вертикальных границ осей (SetVerticalExtents) на всю высоту здания, чтобы оси не пропадали на верхних этажах.
    /// 2. Настройка кружков (Bubble) и выравнивание концов осей на планах (по ГОСТ 21.501-93):
    ///    - Цифровые оси (1, 2, 3...): кружки строго СНИЗУ на одной линии, верхние кружки скрыты.
    ///    - Буквенные оси (А, Б, В...): кружки строго СЛЕВА на одной линии, правые кружки скрыты.
    /// 3. Простановка двух рядов размерных цепочек между осями (шаги осей + общий габарит между крайними осями)
    ///    снизу и слева от здания.
    /// 4. Точное скрытие только действительно лишних / неиспользуемых осей на каждом этаже (например, осей нижних уступов
    ///    на верхних ступенчатых этажах или случайных осей вне конструкций этажа) без скрытия рабочих осей этажа.
    /// </summary>
    public static class GostGridManager
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const string GostAxisDimTag = "GOST_AXIS_DIM";

        // Порог слияния только реально дублирующихся осей (на одной и той же стене < 280 мм)
        private const double DuplicateGridThresholdFt = 280.0 * MmToFeet;

        // ГОСТ 21.101-97 п. 4.3.2: Буквенные обозначения осей выполняют прописными буквами русского алфавита,
        // за исключением букв: Ё, З, Й, О, Х, Ц, Ч, Щ, Ъ, Ы, Ь
        private static readonly string[] GostCyrillicLetters =
        [
            "А", "Б", "В", "Г", "Д", "Е", "Ж", "И", "К", "Л", "М", "Н", "П", "Р", "С", "Т", "У", "Ф", "Ш", "Э", "Ю", "Я"
        ];

        private sealed record GridCandidate(
            Grid Grid,
            Line Line,
            bool IsVertical,
            double Coord,
            bool IsUsed,
            double AlignmentErrorFt);

        /// <summary>
        /// Упорядочивает и правильно подписывает существующие оси проекта по ГОСТ 21.101-97,
        /// а также растягивает их вертикальный 3D-диапазон на все этажи здания.
        /// </summary>
        public static (int renamedNumbers, int renamedLetters) RenumberExistingGridsByGost(
            Document doc,
            BoundingBoxXYZ? buildingWorldBox = null)
        {
            List<Grid> grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .Where(g => !g.ViewSpecific && g.Curve is Line)
                .ToList();

            if (grids.Count == 0)
            {
                return (0, 0);
            }

            BoundingBoxXYZ? bBox = buildingWorldBox ?? SheetLayoutEngine.ComputeBuildingWorldBoundingBox(doc);

            // Собираем все конструкции здания во всем проекте (стены, колонны, балки, перекрытия, крыши)
            List<Wall> allWalls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .OfType<Wall>()
                .Where(w => !SheetLayoutEngine.IsSitePlanElement(doc, w))
                .ToList();

            List<Element> allColumns = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(new LogicalOrFilter(
                    new ElementCategoryFilter(BuiltInCategory.OST_Columns),
                    new ElementCategoryFilter(BuiltInCategory.OST_StructuralColumns)))
                .Where(c => !SheetLayoutEngine.IsSitePlanElement(doc, c))
                .ToList();

            List<Element> allBeams = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .WhereElementIsNotElementType()
                .Where(b => !SheetLayoutEngine.IsSitePlanElement(doc, b))
                .ToList();

            List<Element> allSlabs = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(new LogicalOrFilter(
                    new ElementCategoryFilter(BuiltInCategory.OST_Floors),
                    new ElementCategoryFilter(BuiltInCategory.OST_Roofs)))
                .Where(s => !SheetLayoutEngine.IsSitePlanElement(doc, s))
                .ToList();

            List<GridCandidate> vertCandidates = [];
            List<GridCandidate> horizCandidates = [];

            foreach (Grid g in grids)
            {
                if (g.Curve is not Line line) continue;

                XYZ p0 = line.GetEndPoint(0);
                XYZ p1 = line.GetEndPoint(1);
                XYZ dir = (p1 - p0).Normalize();

                bool isVertical = Math.Abs(dir.Y) >= Math.Abs(dir.X);
                double coord = isVertical ? (p0.X + p1.X) * 0.5 : (p0.Y + p1.Y) * 0.5;

                bool isUsed = EvaluateGridUsage(
                    line, isVertical, coord, bBox,
                    allWalls, allColumns, allBeams, allSlabs,
                    out double errFt);

                GridCandidate cand = new GridCandidate(g, line, isVertical, coord, isUsed, errFt);

                if (isVertical)
                    vertCandidates.Add(cand);
                else
                    horizCandidates.Add(cand);
            }

            // Отбираем рабочие оси здания (убирая только явные дубликаты на одной стене < 280 мм)
            List<GridCandidate> usedVert = FilterAndDeduplicateGrids(
                vertCandidates,
                bBox?.Min.X,
                bBox?.Max.X,
                DuplicateGridThresholdFt);

            List<GridCandidate> usedHoriz = FilterAndDeduplicateGrids(
                horizCandidates,
                bBox?.Min.Y,
                bBox?.Max.Y,
                DuplicateGridThresholdFt);

            HashSet<ElementId> usedIds = usedVert.Select(c => c.Grid.Id)
                .Concat(usedHoriz.Select(c => c.Grid.Id))
                .ToHashSet();

            List<Grid> unusedGrids = grids.Where(g => !usedIds.Contains(g.Id)).ToList();

            bool ownTransaction = !doc.IsModifiable;
            Transaction? tx = null;

            try
            {
                if (ownTransaction)
                {
                    tx = new Transaction(doc, "Маркировка координационных осей по ГОСТ");
                    SheetLayoutEngine.ConfigureSilentTransaction(tx);
                    tx.Start();
                }

                // Растягиваем вертикальный 3D-диапазон всех осей на всю высоту здания (+6 м вниз и вверх),
                // чтобы оси гарантированно пересекали секущий диапазон ВСЕХ этажей (с 1-го по верхний этаж)
                if (bBox != null)
                {
                    double minZ = bBox.Min.Z - (6000.0 * MmToFeet);
                    double maxZ = bBox.Max.Z + (6000.0 * MmToFeet);
                    foreach (Grid g in grids)
                    {
                        try { g.SetVerticalExtents(minZ, maxZ); } catch { }
                    }
                }

                // 1. Временные уникальные имена для исключения конфликта имен в Revit
                int tmpIdx = 1;
                foreach (Grid g in grids)
                {
                    try
                    {
                        g.Name = $"__TMP_G_{tmpIdx++}_{g.Id.Value}__";
                    }
                    catch { }
                }

                int renamedNumCount = 0;
                int renamedLetCount = 0;

                // 2. Цифровые оси: 1, 2, 3... строго слева направо (по возрастанию X)
                for (int i = 0; i < usedVert.Count; i++)
                {
                    string targetName = (i + 1).ToString(CultureInfo.InvariantCulture);
                    try
                    {
                        usedVert[i].Grid.Name = targetName;
                        renamedNumCount++;
                    }
                    catch
                    {
                        try { usedVert[i].Grid.Name = $"{targetName}_{i + 1}"; } catch { }
                    }
                }

                // 3. Буквенные оси: А, Б, В, Г... строго снизу вверх (по возрастанию Y)
                for (int i = 0; i < usedHoriz.Count; i++)
                {
                    string targetName = GetGostLetter(i);
                    try
                    {
                        usedHoriz[i].Grid.Name = targetName;
                        renamedLetCount++;
                    }
                    catch
                    {
                        try { usedHoriz[i].Grid.Name = $"{targetName}_{i + 1}"; } catch { }
                    }
                }

                // 4. Неиспользуемые / лишние оси получают вспомогательные имена Доп.1, Доп.2...,
                // чтобы не занимать основные номера и быть скрытыми на видах
                for (int i = 0; i < unusedGrids.Count; i++)
                {
                    try
                    {
                        unusedGrids[i].Name = $"Доп.{i + 1}";
                    }
                    catch { }
                }

                if (ownTransaction && tx != null)
                {
                    tx.Commit();
                }

                return (renamedNumCount, renamedLetCount);
            }
            catch
            {
                if (ownTransaction && tx != null && tx.HasStarted() && !tx.HasEnded())
                {
                    try { tx.RollBack(); } catch { }
                }
                return (0, 0);
            }
            finally
            {
                tx?.Dispose();
            }
        }

        public static string GetGostLetter(int index)
        {
            if (index < 0) index = 0;
            if (index < GostCyrillicLetters.Length)
            {
                return GostCyrillicLetters[index];
            }
            int primary = index / GostCyrillicLetters.Length;
            int rem = index % GostCyrillicLetters.Length;
            return $"{GostCyrillicLetters[rem]}/{primary}";
        }

        public static bool IsExtraUnusedGridName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            return name.StartsWith("Доп.", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("__TMP_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Комплексная обработка осей на конкретном виде:
        /// - скрывает неиспользуемые и лишние оси на данном этаже/разрезе/фасаде;
        /// - на планах этажей выравнивает кружки осей (цифры снизу, буквы слева) и строит размерные цепочки по ГОСТ.
        /// </summary>
        public static void HideUnusedGridsInView(Document doc, View view, BoundingBoxXYZ buildingWorldBox)
        {
            if (view == null || view.IsTemplate || !view.CanBePrinted) return;

            try
            {
                // На генеральном плане (ГП-1) координационные оси здания скрываются по ГОСТ 21.508-93
                string viewName = view.Name ?? string.Empty;
                if (viewName.StartsWith("ГП_", StringComparison.OrdinalIgnoreCase) ||
                    viewName.Contains("Генеральный план", StringComparison.OrdinalIgnoreCase) ||
                    viewName.Contains("Ситуацион", StringComparison.OrdinalIgnoreCase))
                {
                    ElementId gridCatId = new ElementId(BuiltInCategory.OST_Grids);
                    if (view.CanCategoryBeHidden(gridCatId))
                    {
                        view.SetCategoryHidden(gridCatId, true);
                    }
                    return;
                }

                List<Grid> allGrids = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .Where(g => !g.ViewSpecific && g.Curve is Line)
                    .ToList();

                if (allGrids.Count == 0) return;

                int scale = Math.Max(1, view.Scale);
                double scaleFt = scale * MmToFeet;

                if (view is ViewPlan planView &&
                    view.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan)
                {
                    // Гарантируем, что категория осей включена на плане этажа
                    ElementId gridCatId = new ElementId(BuiltInCategory.OST_Grids);
                    if (view.CanCategoryBeHidden(gridCatId) && view.GetCategoryHidden(gridCatId))
                    {
                        try { view.SetCategoryHidden(gridCatId, false); } catch { }
                    }

                    ConfigureFloorPlanGridsAndDimensions(doc, planView, allGrids, buildingWorldBox, scaleFt);
                }
                else if (view is ViewSection vs && view.ViewType is ViewType.Section or ViewType.Elevation)
                {
                    HideUnusedGridsInSectionOrElevation(doc, vs, allGrids, buildingWorldBox);
                }
            }
            catch
            {
                // Защита от исключений специфических видов
            }
        }

        private static void ConfigureFloorPlanGridsAndDimensions(
            Document doc,
            ViewPlan planView,
            List<Grid> allGrids,
            BoundingBoxXYZ buildingWorldBox,
            double scaleFt)
        {
            // Временно отключаем CropBoxActive перед сбором элементов на этаже,
            // чтобы старая или сдвинутая рамка подрезки не скрыла стены/колонны этого этажа от коллектора!
            bool wasCropActive = false;
            try
            {
                wasCropActive = planView.CropBoxActive;
                if (wasCropActive)
                {
                    planView.CropBoxActive = false;
                }
            }
            catch { }

            // 1. Собираем конструкции этого этажа (используя как видимые элементы вида, так и проверку по Z-уровню этажа)
            (List<Wall> viewWalls, List<Element> viewColumns, List<Element> viewBeams, List<Element> viewSlabs) =
                CollectFloorBuildingElements(doc, planView);

            try
            {
                if (wasCropActive)
                {
                    planView.CropBoxActive = true;
                }
            }
            catch { }

            // Вычисляем реальный габарит конструкций именно этого этажа (учитывая ступенчатые/террасные этажи)
            BoundingBoxXYZ levelBBox = ComputeViewBuildingBoundingBox(
                planView, viewWalls, viewColumns, viewBeams, viewSlabs, buildingWorldBox);

            List<GridCandidate> vertCands = [];
            List<GridCandidate> horizCands = [];

            foreach (Grid g in allGrids)
            {
                if (g.Curve is not Line line) continue;

                XYZ p0 = line.GetEndPoint(0);
                XYZ p1 = line.GetEndPoint(1);
                XYZ dir = (p1 - p0).Normalize();

                bool isVertical = Math.Abs(dir.Y) >= Math.Abs(dir.X);
                double coord = isVertical ? (p0.X + p1.X) * 0.5 : (p0.Y + p1.Y) * 0.5;

                bool isGloballyExtra = IsExtraUnusedGridName(g.Name);
                double errFt = 999.0;
                bool usedOnLevel = !isGloballyExtra &&
                                   EvaluateGridUsage(
                                       line, isVertical, coord, levelBBox,
                                       viewWalls, viewColumns, viewBeams, viewSlabs,
                                       out errFt);

                GridCandidate cand = new GridCandidate(g, line, isVertical, coord, usedOnLevel, errFt);
                if (isVertical)
                    vertCands.Add(cand);
                else
                    horizCands.Add(cand);
            }

            // Фильтруем используемые на данном этаже оси (сливаем только полные дубликаты < 280 мм,
            // никогда не удаляя рабочие оси ступенчатых этажей!)
            List<GridCandidate> activeVert = FilterAndDeduplicateGrids(
                vertCands.Where(c => !IsExtraUnusedGridName(c.Grid.Name)).ToList(),
                levelBBox.Min.X,
                levelBBox.Max.X,
                DuplicateGridThresholdFt);

            List<GridCandidate> activeHoriz = FilterAndDeduplicateGrids(
                horizCands.Where(c => !IsExtraUnusedGridName(c.Grid.Name)).ToList(),
                levelBBox.Min.Y,
                levelBBox.Max.Y,
                DuplicateGridThresholdFt);

            HashSet<ElementId> activeIds = activeVert.Select(c => c.Grid.Id)
                .Concat(activeHoriz.Select(c => c.Grid.Id))
                .ToHashSet();

            // 2. Показываем все рабочие оси этажа и скрываем только действительно лишние (не используемые на этом этаже)
            List<ElementId> toHide = [];
            List<ElementId> toUnhide = [];

            foreach (Grid g in allGrids)
            {
                if (activeIds.Contains(g.Id))
                {
                    if (g.IsHidden(planView))
                        toUnhide.Add(g.Id);
                }
                else
                {
                    if (!g.IsHidden(planView) && g.CanBeHidden(planView))
                        toHide.Add(g.Id);
                }
            }

            if (toUnhide.Count > 0)
            {
                try { planView.UnhideElements(toUnhide); } catch { }
            }
            if (toHide.Count > 0)
            {
                try { planView.HideElements(toHide); } catch { }
            }

            // 3. Определяем габарит здания и активных осей для выравнивания кружков и размерных цепочек
            double bMinX = levelBBox.Min.X;
            double bMaxX = levelBBox.Max.X;
            double bMinY = levelBBox.Min.Y;
            double bMaxY = levelBBox.Max.Y;

            if (activeVert.Count > 0)
            {
                bMinX = Math.Min(bMinX, activeVert.First().Coord);
                bMaxX = Math.Max(bMaxX, activeVert.Last().Coord);
            }
            if (activeHoriz.Count > 0)
            {
                bMinY = Math.Min(bMinY, activeHoriz.First().Coord);
                bMaxY = Math.Max(bMaxY, activeHoriz.Last().Coord);
            }

            // Отступы по ГОСТ 21.501-93 (как на образце):
            // - Первая размерная линия (шаг осей): 8.5 мм от контура здания
            // - Вторая размерная линия (общий размер между крайними осями): 15.0 мм от контура здания
            // - Положение кружков осей: 21.0 мм от контура здания (снизу для цифр, слева для букв)
            // - Короткий выступ осей сверху и справа (без кружков): 3.0 мм за контур здания
            double yDimStep = bMinY - (8.5 * scaleFt);
            double yDimTotal = bMinY - (15.0 * scaleFt);
            double targetBottomY = bMinY - (21.0 * scaleFt);
            double targetTopY = bMaxY + (3.0 * scaleFt);

            double xDimStep = bMinX - (8.5 * scaleFt);
            double xDimTotal = bMinX - (15.0 * scaleFt);
            double targetLeftX = bMinX - (21.0 * scaleFt);
            double targetRightX = bMaxX + (3.0 * scaleFt);

            // 4. Выравниваем 2D-концы вертикальных (цифровых) осей и включаем кружки ТОЛЬКО снизу
            foreach (GridCandidate vc in activeVert)
            {
                ConfigureGridInPlan(vc.Grid, planView, isVertical: true,
                    minCoord: targetBottomY, maxCoord: targetTopY);
            }

            // 5. Выравниваем 2D-концы горизонтальных (буквенных) осей и включаем кружки ТОЛЬКО слева
            foreach (GridCandidate hc in activeHoriz)
            {
                ConfigureGridInPlan(hc.Grid, planView, isVertical: false,
                    minCoord: targetLeftX, maxCoord: targetRightX);
            }

            // 6. Вычерчиваем размерные цепочки между осями снизу и слева (шаги + общий размер по ГОСТ)
            DrawPlanGridDimensionChains(
                doc, planView,
                activeVert, activeHoriz,
                yDimStep, yDimTotal,
                xDimStep, xDimTotal,
                scaleFt);

            // 7. Настраиваем область подрезки (CropBox) плана с запасом слева и снизу под кружки осей и размеры,
            // и отключаем аннотационную подрезку, чтобы Revit не обрезал кружки осей!
            ApplyPlanGostCropBox(planView, bMinX, bMaxX, bMinY, bMaxY, scaleFt);
        }

        /// <summary>
        /// Собирает конструкции этажа как из видимых элементов плана, так и по высотной отметке уровня GenLevel,
        /// чтобы ни одна стена, колонна или плита этажа не была пропущена из-за настроек секущего диапазона или старой подрезки.
        /// </summary>
        private static (List<Wall> Walls, List<Element> Columns, List<Element> Beams, List<Element> Slabs)
            CollectFloorBuildingElements(Document doc, ViewPlan planView)
        {
            HashSet<ElementId> wallIds = [];
            List<Wall> walls = [];

            foreach (Wall w in new FilteredElementCollector(doc, planView.Id)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .OfType<Wall>()
                .Where(w => !SheetLayoutEngine.IsSitePlanElement(doc, w)))
            {
                if (wallIds.Add(w.Id)) walls.Add(w);
            }

            HashSet<ElementId> colIds = [];
            List<Element> columns = [];
            foreach (Element c in new FilteredElementCollector(doc, planView.Id)
                .WhereElementIsNotElementType()
                .WherePasses(new LogicalOrFilter(
                    new ElementCategoryFilter(BuiltInCategory.OST_Columns),
                    new ElementCategoryFilter(BuiltInCategory.OST_StructuralColumns)))
                .Where(c => !SheetLayoutEngine.IsSitePlanElement(doc, c)))
            {
                if (colIds.Add(c.Id)) columns.Add(c);
            }

            HashSet<ElementId> beamIds = [];
            List<Element> beams = [];
            foreach (Element b in new FilteredElementCollector(doc, planView.Id)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .WhereElementIsNotElementType()
                .Where(b => !SheetLayoutEngine.IsSitePlanElement(doc, b)))
            {
                if (beamIds.Add(b.Id)) beams.Add(b);
            }

            HashSet<ElementId> slabIds = [];
            List<Element> slabs = [];
            foreach (Element s in new FilteredElementCollector(doc, planView.Id)
                .WhereElementIsNotElementType()
                .WherePasses(new LogicalOrFilter(
                    new ElementCategoryFilter(BuiltInCategory.OST_Floors),
                    new ElementCategoryFilter(BuiltInCategory.OST_Roofs)))
                .Where(s => !SheetLayoutEngine.IsSitePlanElement(doc, s)))
            {
                if (slabIds.Add(s.Id)) slabs.Add(s);
            }

            // Дополнительно проверяем элементы по Z-отметке уровня GenLevel (на случай, если в виде была узкая подрезка)
            Level? level = planView.GenLevel;
            if (level != null)
            {
                double levelZ = level.Elevation;
                double minZCheck = levelZ - (400.0 * MmToFeet);
                double maxZCheck = levelZ + (2600.0 * MmToFeet);

                foreach (Wall w in new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .OfType<Wall>()
                    .Where(w => !SheetLayoutEngine.IsSitePlanElement(doc, w)))
                {
                    if (wallIds.Contains(w.Id)) continue;
                    if (w.LevelId == level.Id)
                    {
                        wallIds.Add(w.Id);
                        walls.Add(w);
                        continue;
                    }
                    BoundingBoxXYZ? bb = w.get_BoundingBox(null);
                    if (bb != null && bb.Min.Z <= maxZCheck && bb.Max.Z >= levelZ + (300.0 * MmToFeet))
                    {
                        wallIds.Add(w.Id);
                        walls.Add(w);
                    }
                }

                foreach (Element c in new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(new LogicalOrFilter(
                        new ElementCategoryFilter(BuiltInCategory.OST_Columns),
                        new ElementCategoryFilter(BuiltInCategory.OST_StructuralColumns)))
                    .Where(c => !SheetLayoutEngine.IsSitePlanElement(doc, c)))
                {
                    if (colIds.Contains(c.Id)) continue;
                    BoundingBoxXYZ? bb = c.get_BoundingBox(null);
                    if (bb != null && bb.Min.Z <= maxZCheck && bb.Max.Z >= levelZ + (200.0 * MmToFeet))
                    {
                        colIds.Add(c.Id);
                        columns.Add(c);
                    }
                }

                foreach (Element s in new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(new LogicalOrFilter(
                        new ElementCategoryFilter(BuiltInCategory.OST_Floors),
                        new ElementCategoryFilter(BuiltInCategory.OST_Roofs)))
                    .Where(s => !SheetLayoutEngine.IsSitePlanElement(doc, s)))
                {
                    if (slabIds.Contains(s.Id)) continue;
                    if (s.LevelId == level.Id)
                    {
                        slabIds.Add(s.Id);
                        slabs.Add(s);
                        continue;
                    }
                    BoundingBoxXYZ? bb = s.get_BoundingBox(null);
                    if (bb != null && bb.Min.Z <= levelZ + (800.0 * MmToFeet) && bb.Max.Z >= minZCheck)
                    {
                        slabIds.Add(s.Id);
                        slabs.Add(s);
                    }
                }
            }

            return (walls, columns, beams, slabs);
        }

        private static void ConfigureGridInPlan(
            Grid grid,
            ViewPlan planView,
            bool isVertical,
            double minCoord,
            double maxCoord)
        {
            try
            {
                // Переводим оба конца оси в видоспецифичный (2D) режим для данного плана
                try { grid.SetDatumExtentType(DatumEnds.End0, planView, DatumExtentType.ViewSpecific); } catch { }
                try { grid.SetDatumExtentType(DatumEnds.End1, planView, DatumExtentType.ViewSpecific); } catch { }

                IList<Curve>? viewCurves = null;
                try { viewCurves = grid.GetCurvesInView(DatumExtentType.ViewSpecific, planView); } catch { }

                Line? baseLine = (viewCurves != null && viewCurves.Count > 0 ? viewCurves[0] as Line : null)
                                 ?? (grid.Curve as Line);

                if (baseLine == null) return;

                XYZ p0 = baseLine.GetEndPoint(0);
                XYZ p1 = baseLine.GetEndPoint(1);
                XYZ dir = (p1 - p0).Normalize();

                if (isVertical)
                {
                    // Кружок строго СНИЗУ (меньший Y), сверху кружок скрыт
                    DatumEnds bottomEnd = p0.Y <= p1.Y ? DatumEnds.End0 : DatumEnds.End1;
                    DatumEnds topEnd = bottomEnd == DatumEnds.End0 ? DatumEnds.End1 : DatumEnds.End0;

                    try { grid.ShowBubbleInView(bottomEnd, planView); } catch { }
                    try { grid.HideBubbleInView(topEnd, planView); } catch { }

                    if (Math.Abs(dir.Y) > 1e-4 && maxCoord > minCoord + 0.5)
                    {
                        double tBottom = (minCoord - p0.Y) / dir.Y;
                        double tTop = (maxCoord - p0.Y) / dir.Y;

                        XYZ newP0 = dir.Y > 0 ? p0 + (tBottom * dir) : p0 + (tTop * dir);
                        XYZ newP1 = dir.Y > 0 ? p0 + (tTop * dir) : p0 + (tBottom * dir);

                        if (newP0.DistanceTo(newP1) > 0.5)
                        {
                            Line newLine = Line.CreateBound(newP0, newP1);
                            if (grid.IsCurveValidInView(DatumExtentType.ViewSpecific, planView, newLine))
                            {
                                grid.SetCurveInView(DatumExtentType.ViewSpecific, planView, newLine);
                            }
                        }
                    }
                }
                else
                {
                    // Кружок строго СЛЕВА (меньший X), справа кружок скрыт
                    DatumEnds leftEnd = p0.X <= p1.X ? DatumEnds.End0 : DatumEnds.End1;
                    DatumEnds rightEnd = leftEnd == DatumEnds.End0 ? DatumEnds.End1 : DatumEnds.End0;

                    try { grid.ShowBubbleInView(leftEnd, planView); } catch { }
                    try { grid.HideBubbleInView(rightEnd, planView); } catch { }

                    if (Math.Abs(dir.X) > 1e-4 && maxCoord > minCoord + 0.5)
                    {
                        double tLeft = (minCoord - p0.X) / dir.X;
                        double tRight = (maxCoord - p0.X) / dir.X;

                        XYZ newP0 = dir.X > 0 ? p0 + (tLeft * dir) : p0 + (tRight * dir);
                        XYZ newP1 = dir.X > 0 ? p0 + (tRight * dir) : p0 + (tLeft * dir);

                        if (newP0.DistanceTo(newP1) > 0.5)
                        {
                            Line newLine = Line.CreateBound(newP0, newP1);
                            if (grid.IsCurveValidInView(DatumExtentType.ViewSpecific, planView, newLine))
                            {
                                grid.SetCurveInView(DatumExtentType.ViewSpecific, planView, newLine);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void DrawPlanGridDimensionChains(
            Document doc,
            ViewPlan planView,
            List<GridCandidate> activeVert,
            List<GridCandidate> activeHoriz,
            double yDimStep,
            double yDimTotal,
            double xDimStep,
            double xDimTotal,
            double scaleFt)
        {
            // 1. Удаляем прежние размерные цепочки осей на этом плане (для 100% идемпотентности)
            CleanPreviousPlanAxisDimensions(doc, planView);

            double z = planView.GenLevel?.Elevation ?? 0.0;
            ElementId fontDimId = GetOrCreatePlanDimTextType(doc);
            double tickHalf = 1.1 * scaleFt; // засечка под 45°
            double extOverhang = 1.4 * scaleFt;

            // 2. Горизонтальные размерные цепочки СНИЗУ между цифровыми осями (1, 2, 3...)
            if (activeVert.Count >= 2)
            {
                double xFirst = activeVert.First().Coord;
                double xLast = activeVert.Last().Coord;

                // Линия 1: цепочка шагов между соседними осями
                DrawPlanDetailLine(doc, planView,
                    new XYZ(xFirst - extOverhang, yDimStep, z),
                    new XYZ(xLast + extOverhang, yDimStep, z));

                for (int i = 0; i < activeVert.Count; i++)
                {
                    double x = activeVert[i].Coord;
                    DrawPlanDetailLine(doc, planView,
                        new XYZ(x - tickHalf, yDimStep - tickHalf, z),
                        new XYZ(x + tickHalf, yDimStep + tickHalf, z));

                    if (i < activeVert.Count - 1)
                    {
                        double xNext = activeVert[i + 1].Coord;
                        int stepMm = (int)Math.Round(((xNext - x) / MmToFeet) / 10.0) * 10;
                        if (stepMm > 0)
                        {
                            double midX = (x + xNext) * 0.5;
                            CreatePlanDimText(doc, planView,
                                new XYZ(midX, yDimStep + (2.8 * scaleFt), z),
                                FormatMillimetersGost(stepMm),
                                fontDimId,
                                rotationRad: 0.0);
                        }
                    }
                }

                // Линия 2: общий размер между крайними цифровыми осями (1 .. N)
                int totalWidthMm = (int)Math.Round(((xLast - xFirst) / MmToFeet) / 10.0) * 10;
                if (totalWidthMm > 0 && activeVert.Count > 2)
                {
                    DrawPlanDetailLine(doc, planView,
                        new XYZ(xFirst - extOverhang, yDimTotal, z),
                        new XYZ(xLast + extOverhang, yDimTotal, z));

                    DrawPlanDetailLine(doc, planView,
                        new XYZ(xFirst - tickHalf, yDimTotal - tickHalf, z),
                        new XYZ(xFirst + tickHalf, yDimTotal + tickHalf, z));
                    DrawPlanDetailLine(doc, planView,
                        new XYZ(xLast - tickHalf, yDimTotal - tickHalf, z),
                        new XYZ(xLast + tickHalf, yDimTotal + tickHalf, z));

                    CreatePlanDimText(doc, planView,
                        new XYZ((xFirst + xLast) * 0.5, yDimTotal + (2.8 * scaleFt), z),
                        FormatMillimetersGost(totalWidthMm),
                        fontDimId,
                        rotationRad: 0.0);
                }
            }

            // 3. Вертикальные размерные цепочки СЛЕВА между буквенными осями (А, Б, В...)
            if (activeHoriz.Count >= 2)
            {
                double yFirst = activeHoriz.First().Coord;
                double yLast = activeHoriz.Last().Coord;

                // Линия 1: цепочка шагов между соседними буквенными осями
                DrawPlanDetailLine(doc, planView,
                    new XYZ(xDimStep, yFirst - extOverhang, z),
                    new XYZ(xDimStep, yLast + extOverhang, z));

                for (int i = 0; i < activeHoriz.Count; i++)
                {
                    double y = activeHoriz[i].Coord;
                    DrawPlanDetailLine(doc, planView,
                        new XYZ(xDimStep - tickHalf, y - tickHalf, z),
                        new XYZ(xDimStep + tickHalf, y + tickHalf, z));

                    if (i < activeHoriz.Count - 1)
                    {
                        double yNext = activeHoriz[i + 1].Coord;
                        int stepMm = (int)Math.Round(((yNext - y) / MmToFeet) / 10.0) * 10;
                        if (stepMm > 0)
                        {
                            double midY = (y + yNext) * 0.5;
                            CreatePlanDimText(doc, planView,
                                new XYZ(xDimStep - (2.8 * scaleFt), midY, z),
                                FormatMillimetersGost(stepMm),
                                fontDimId,
                                rotationRad: Math.PI / 2.0);
                        }
                    }
                }

                // Линия 2: общий размер между крайними буквенными осями (А .. Я)
                int totalLenMm = (int)Math.Round(((yLast - yFirst) / MmToFeet) / 10.0) * 10;
                if (totalLenMm > 0 && activeHoriz.Count > 2)
                {
                    DrawPlanDetailLine(doc, planView,
                        new XYZ(xDimTotal, yFirst - extOverhang, z),
                        new XYZ(xDimTotal, yLast + extOverhang, z));

                    DrawPlanDetailLine(doc, planView,
                        new XYZ(xDimTotal - tickHalf, yFirst - tickHalf, z),
                        new XYZ(xDimTotal + tickHalf, yFirst + tickHalf, z));
                    DrawPlanDetailLine(doc, planView,
                        new XYZ(xDimTotal - tickHalf, yLast - tickHalf, z),
                        new XYZ(xDimTotal + tickHalf, yLast + tickHalf, z));

                    CreatePlanDimText(doc, planView,
                        new XYZ(xDimTotal - (2.8 * scaleFt), (yFirst + yLast) * 0.5, z),
                        FormatMillimetersGost(totalLenMm),
                        fontDimId,
                        rotationRad: Math.PI / 2.0);
                }
            }
        }

        private static void ApplyPlanGostCropBox(
            ViewPlan planView,
            double bMinX,
            double bMaxX,
            double bMinY,
            double bMaxY,
            double scaleFt)
        {
            try
            {
                if (planView.CropBox == null) return;

                BoundingBoxXYZ curCrop = planView.CropBox;
                Transform t = curCrop.Transform;
                Transform inv = t.Inverse;

                // Снизу и слева оставляем 30 мм на бумаге под две размерные линии и кружки осей Ø8 мм,
                // сверху и справа — аккуратный отступ 7 мм
                double leftMarginFt = 30.0 * scaleFt;
                double bottomMarginFt = 30.0 * scaleFt;
                double rightMarginFt = 7.0 * scaleFt;
                double topMarginFt = 7.0 * scaleFt;

                XYZ pMinWorld = new XYZ(bMinX - leftMarginFt, bMinY - bottomMarginFt, 0.0);
                XYZ pMaxWorld = new XYZ(bMaxX + rightMarginFt, bMaxY + topMarginFt, 0.0);

                XYZ localA = inv.OfPoint(pMinWorld);
                XYZ localB = inv.OfPoint(pMaxWorld);

                double lMinX = Math.Min(localA.X, localB.X);
                double lMaxX = Math.Max(localA.X, localB.X);
                double lMinY = Math.Min(localA.Y, localB.Y);
                double lMaxY = Math.Max(localA.Y, localB.Y);

                if (lMaxX - lMinX > 1.0 && lMaxY - lMinY > 1.0)
                {
                    planView.CropBox = new BoundingBoxXYZ
                    {
                        Transform = t,
                        Min = new XYZ(lMinX, lMinY, curCrop.Min.Z),
                        Max = new XYZ(lMaxX, lMaxY, curCrop.Max.Z)
                    };
                    planView.CropBoxActive = true;
                    planView.CropBoxVisible = false;
                }

                // Отключаем аннотационную подрезку на планах, чтобы кружки осей гарантированно отображались
                Parameter? annoCrop = planView.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (annoCrop is { IsReadOnly: false })
                {
                    annoCrop.Set(0);
                }
            }
            catch { }
        }

        /// <summary>
        /// Возвращает список активных координационных осей для отрисовки внизу разреза или фасада по ГОСТ 21.501-93.
        /// Не создает искусственных осей, если их нет в модели!
        /// </summary>
        public static List<GostGridItem> GetUsedGridsForSectionOrElevation(
            Document doc,
            ViewSection view,
            XYZ origin,
            XYZ rightDir,
            double uMin,
            double uMax,
            double scaleFt)
        {
            List<Grid> allGrids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .Where(g => !g.ViewSpecific && g.Curve is Line && !IsExtraUnusedGridName(g.Name))
                .ToList();

            if (allGrids.Count == 0)
            {
                return [];
            }

            XYZ viewDir = view.ViewDirection.Normalize();
            double toleranceFt = 3.0; // ~0.9 м от габарита здания
            List<(double U, string Name, Grid Grid)> intersecting = [];

            foreach (Grid g in allGrids)
            {
                if (g.Curve is not Line gLine) continue;

                XYZ p1 = gLine.GetEndPoint(0);
                XYZ p2 = gLine.GetEndPoint(1);
                XYZ vLine = p2 - p1;
                double denom = vLine.DotProduct(viewDir);

                if (Math.Abs(denom) > 0.15)
                {
                    double tParam = (origin - p1).DotProduct(viewDir) / denom;
                    XYZ pInt = p1 + (tParam * vLine);
                    double u = (pInt - origin).DotProduct(rightDir);

                    if (u >= uMin - toleranceFt && u <= uMax + toleranceFt)
                    {
                        intersecting.Add((u, g.Name, g));
                    }
                }
            }

            if (intersecting.Count == 0)
            {
                return [];
            }

            var sorted = intersecting.OrderBy(x => x.U).ToList();
            double minSpacingFt = Math.Max(400.0 * MmToFeet, 8.2 * scaleFt);
            List<GostGridItem> result = [];

            for (int i = 0; i < sorted.Count; i++)
            {
                bool isFirst = i == 0;
                bool isLast = i == sorted.Count - 1;

                if (isFirst)
                {
                    result.Add(new GostGridItem(sorted[i].U, sorted[i].Name));
                }
                else if (isLast)
                {
                    if (result.Count > 1 && Math.Abs(sorted[i].U - result[^1].U) < minSpacingFt)
                    {
                        result[^1] = new GostGridItem(sorted[i].U, sorted[i].Name);
                    }
                    else if (Math.Abs(sorted[i].U - result[^1].U) >= DuplicateGridThresholdFt)
                    {
                        result.Add(new GostGridItem(sorted[i].U, sorted[i].Name));
                    }
                }
                else
                {
                    double distFromPrev = Math.Abs(sorted[i].U - result[^1].U);
                    double distToLast = Math.Abs(sorted[^1].U - sorted[i].U);
                    if (distFromPrev >= minSpacingFt && distToLast >= minSpacingFt)
                    {
                        result.Add(new GostGridItem(sorted[i].U, sorted[i].Name));
                    }
                }
            }

            return result;
        }

        private static void HideUnusedGridsInSectionOrElevation(
            Document doc,
            ViewSection vs,
            List<Grid> allGrids,
            BoundingBoxXYZ buildingWorldBox)
        {
            XYZ rightDir = vs.RightDirection.Normalize();
            XYZ origin = vs.Origin;

            XYZ wMin = buildingWorldBox.Min;
            XYZ wMax = buildingWorldBox.Max;
            XYZ[] corners =
            [
                new XYZ(wMin.X, wMin.Y, wMin.Z), new XYZ(wMax.X, wMin.Y, wMin.Z),
                new XYZ(wMax.X, wMax.Y, wMin.Z), new XYZ(wMin.X, wMax.Y, wMin.Z),
                new XYZ(wMin.X, wMin.Y, wMax.Z), new XYZ(wMax.X, wMin.Y, wMax.Z),
                new XYZ(wMax.X, wMax.Y, wMax.Z), new XYZ(wMin.X, wMax.Y, wMax.Z)
            ];

            double uMin = double.MaxValue, uMax = double.MinValue;
            foreach (XYZ corner in corners)
            {
                double u = (corner - origin).DotProduct(rightDir);
                uMin = Math.Min(uMin, u);
                uMax = Math.Max(uMax, u);
            }

            int scale = Math.Max(1, vs.Scale);
            List<GostGridItem> usedInSec = GetUsedGridsForSectionOrElevation(doc, vs, origin, rightDir, uMin, uMax, scale * MmToFeet);
            HashSet<string> allowedNames = usedInSec.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<ElementId> toHide = [];
            foreach (Grid g in allGrids)
            {
                if (!allowedNames.Contains(g.Name) && !g.IsHidden(vs) && g.CanBeHidden(vs))
                {
                    toHide.Add(g.Id);
                }
            }

            if (toHide.Count > 0)
            {
                try { vs.HideElements(toHide); } catch { }
            }
        }

        private static bool EvaluateGridUsage(
            Line gridLine,
            bool isVertical,
            double coord,
            BoundingBoxXYZ? bBox,
            List<Wall> walls,
            List<Element> columns,
            List<Element> beams,
            List<Element> slabs,
            out double minAlignErrorFt)
        {
            minAlignErrorFt = double.MaxValue;

            // 1. Проверяем, попадает ли ось в пределы габаритов конструкций данного этажа/здания (допуск 900 мм)
            if (bBox != null)
            {
                double bTolFt = 900.0 * MmToFeet;
                if (isVertical)
                {
                    if (coord < bBox.Min.X - bTolFt || coord > bBox.Max.X + bTolFt)
                        return false;
                }
                else
                {
                    if (coord < bBox.Min.Y - bTolFt || coord > bBox.Max.Y + bTolFt)
                        return false;
                }

                // Если ось проходит по наружному контуру конструкций данного этажа (в пределах 850 мм от крайней грани этажа) —
                // она является крайней габаритной осью этажа и ВСЕГДА используется на этом этаже!
                double outerEdgeTolFt = 850.0 * MmToFeet;
                double minEdge = isVertical ? bBox.Min.X : bBox.Min.Y;
                double maxEdge = isVertical ? bBox.Max.X : bBox.Max.Y;
                double dOuter = Math.Min(Math.Abs(coord - minEdge), Math.Abs(coord - maxEdge));
                if (dOuter <= outerEdgeTolFt)
                {
                    minAlignErrorFt = Math.Min(minAlignErrorFt, dOuter * 0.5);
                }
            }

            double wallTolFt = 750.0 * MmToFeet;
            double colTolFt = 750.0 * MmToFeet;
            double slabTolFt = 650.0 * MmToFeet;

            // 2. Проверяем совпадение оси со стенами (как по осевой линии, так и по граням BoundingBox и торцам)
            foreach (Wall w in walls)
            {
                double halfThick = 0.0;
                try { halfThick = w.Width * 0.5; } catch { }

                if (w.Location is LocationCurve { Curve: Line wLine })
                {
                    XYZ wp0 = wLine.GetEndPoint(0);
                    XYZ wp1 = wLine.GetEndPoint(1);
                    XYZ wDir = (wp1 - wp0).Normalize();

                    bool wallIsVertical = Math.Abs(wDir.Y) >= Math.Abs(wDir.X);
                    if (wallIsVertical == isVertical)
                    {
                        double wCoord = isVertical ? (wp0.X + wp1.X) * 0.5 : (wp0.Y + wp1.Y) * 0.5;
                        double dCenter = Math.Abs(wCoord - coord);
                        double dFace = Math.Max(0.0, dCenter - halfThick);

                        if (dFace <= wallTolFt)
                        {
                            minAlignErrorFt = Math.Min(minAlignErrorFt, dFace);
                        }
                    }
                    else if (wLine.Length >= 800.0 * MmToFeet)
                    {
                        // Торцы перпендикулярных стен (уступы фасада, углы)
                        double c0 = isVertical ? wp0.X : wp0.Y;
                        double c1 = isVertical ? wp1.X : wp1.Y;
                        double dEnd = Math.Min(Math.Abs(c0 - coord), Math.Abs(c1 - coord));
                        if (dEnd <= 550.0 * MmToFeet)
                        {
                            minAlignErrorFt = Math.Min(minAlignErrorFt, dEnd);
                        }
                    }
                }

                BoundingBoxXYZ? wb = w.get_BoundingBox(null);
                if (wb != null)
                {
                    double cMin = isVertical ? wb.Min.X : wb.Min.Y;
                    double cMax = isVertical ? wb.Max.X : wb.Max.Y;
                    double d = Math.Min(Math.Abs(cMin - coord), Math.Abs(cMax - coord));
                    if (d <= wallTolFt)
                    {
                        minAlignErrorFt = Math.Min(minAlignErrorFt, d);
                    }
                }
            }

            // 3. Проверяем совпадение оси с колоннами
            foreach (Element col in columns)
            {
                XYZ? colPt = null;
                if (col.Location is LocationPoint lp)
                {
                    colPt = lp.Point;
                }
                else
                {
                    BoundingBoxXYZ? cb = col.get_BoundingBox(null);
                    if (cb != null)
                    {
                        colPt = (cb.Min + cb.Max) * 0.5;
                    }
                }

                if (colPt != null)
                {
                    double cCoord = isVertical ? colPt.X : colPt.Y;
                    double d = Math.Abs(cCoord - coord);
                    if (d <= colTolFt)
                    {
                        minAlignErrorFt = Math.Min(minAlignErrorFt, d);
                    }
                }
            }

            // 4. Проверяем совпадение оси с балками
            foreach (Element beam in beams)
            {
                if (beam.Location is LocationCurve { Curve: Line bLine })
                {
                    XYZ bp0 = bLine.GetEndPoint(0);
                    XYZ bp1 = bLine.GetEndPoint(1);
                    XYZ bDir = (bp1 - bp0).Normalize();
                    bool beamIsVert = Math.Abs(bDir.Y) >= Math.Abs(bDir.X);
                    if (beamIsVert == isVertical)
                    {
                        double bCoord = isVertical ? (bp0.X + bp1.X) * 0.5 : (bp0.Y + bp1.Y) * 0.5;
                        double d = Math.Abs(bCoord - coord);
                        if (d <= wallTolFt)
                        {
                            minAlignErrorFt = Math.Min(minAlignErrorFt, d);
                        }
                    }
                }
            }

            // 5. Проверяем совпадение оси с гранями плит перекрытий и крыш данного этажа (для ступенчатых террас)
            foreach (Element slab in slabs)
            {
                BoundingBoxXYZ? sb = slab.get_BoundingBox(null);
                if (sb != null)
                {
                    double sMin = isVertical ? sb.Min.X : sb.Min.Y;
                    double sMax = isVertical ? sb.Max.X : sb.Max.Y;
                    double d = Math.Min(Math.Abs(sMin - coord), Math.Abs(sMax - coord));
                    if (d <= slabTolFt)
                    {
                        minAlignErrorFt = Math.Min(minAlignErrorFt, d);
                    }
                }
            }

            return minAlignErrorFt <= wallTolFt;
        }

        private static List<GridCandidate> FilterAndDeduplicateGrids(
            List<GridCandidate> candidates,
            double? minBuildingBound,
            double? maxBuildingBound,
            double duplicateThresholdFt)
        {
            if (candidates.Count == 0) return [];

            var sortedAll = candidates.OrderBy(c => c.Coord).ToList();
            var used = sortedAll.Where(c => c.IsUsed).ToList();

            // Гарантируем, что крайние оси, ограничивающие габарит этажа (слева/справа или снизу/сверху),
            // ВСЕГДА включены в список рабочих осей этажа!
            if (minBuildingBound.HasValue && maxBuildingBound.HasValue && sortedAll.Count >= 2)
            {
                double minB = minBuildingBound.Value - (1500.0 * MmToFeet);
                double maxB = maxBuildingBound.Value + (1500.0 * MmToFeet);
                var withinFloor = sortedAll.Where(c => c.Coord >= minB && c.Coord <= maxB).ToList();

                if (withinFloor.Count >= 2)
                {
                    GridCandidate firstFloorGrid = withinFloor.First();
                    GridCandidate lastFloorGrid = withinFloor.Last();

                    if (!used.Any(u => u.Grid.Id == firstFloorGrid.Grid.Id))
                    {
                        used.Insert(0, firstFloorGrid);
                    }
                    if (!used.Any(u => u.Grid.Id == lastFloorGrid.Grid.Id))
                    {
                        used.Add(lastFloorGrid);
                    }
                    used = used.OrderBy(c => c.Coord).ToList();
                }
            }

            if (used.Count < 2 && sortedAll.Count >= 2)
            {
                used = [sortedAll.First(), sortedAll.Last()];
            }

            if (used.Count <= 1) return used;

            // Удаляем ТОЛЬКО полные дубликаты (< 280 мм на одной и той же стене),
            // без цепного смещения и с 100% сохранением первой и последней оси этажа!
            List<GridCandidate> deduplicated = [];

            for (int i = 0; i < used.Count; i++)
            {
                GridCandidate cand = used[i];
                bool isFirst = i == 0;
                bool isLast = i == used.Count - 1;

                if (deduplicated.Count == 0 || isFirst)
                {
                    deduplicated.Add(cand);
                    continue;
                }

                GridCandidate prev = deduplicated[^1];
                if (Math.Abs(cand.Coord - prev.Coord) < duplicateThresholdFt)
                {
                    if (isLast)
                    {
                        // Крайнюю последнюю ось всегда сохраняем (заменяя предпоследний дубликат, если он не первый)
                        if (deduplicated.Count > 1)
                        {
                            deduplicated[^1] = cand;
                        }
                        else
                        {
                            deduplicated.Add(cand);
                        }
                    }
                    else if (deduplicated.Count > 1 && cand.AlignmentErrorFt < prev.AlignmentErrorFt)
                    {
                        deduplicated[^1] = cand;
                    }
                }
                else
                {
                    deduplicated.Add(cand);
                }
            }

            return deduplicated;
        }

        private static BoundingBoxXYZ ComputeViewBuildingBoundingBox(
            View view,
            List<Wall> viewWalls,
            List<Element> viewColumns,
            List<Element> viewBeams,
            List<Element> viewSlabs,
            BoundingBoxXYZ fallbackWorldBox)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool found = false;

            void ExpandByElements(IEnumerable<Element> elems)
            {
                foreach (Element el in elems)
                {
                    BoundingBoxXYZ? bb = el.get_BoundingBox(null) ?? el.get_BoundingBox(view);
                    if (bb == null) continue;
                    minX = Math.Min(minX, bb.Min.X);
                    minY = Math.Min(minY, bb.Min.Y);
                    minZ = Math.Min(minZ, bb.Min.Z);
                    maxX = Math.Max(maxX, bb.Max.X);
                    maxY = Math.Max(maxY, bb.Max.Y);
                    maxZ = Math.Max(maxZ, bb.Max.Z);
                    found = true;
                }
            }

            ExpandByElements(viewWalls);
            ExpandByElements(viewColumns);
            ExpandByElements(viewBeams);
            ExpandByElements(viewSlabs);

            if (!found || maxX - minX < 1.0 || maxY - minY < 1.0)
            {
                return fallbackWorldBox;
            }

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private static void CleanPreviousPlanAxisDimensions(Document doc, ViewPlan planView)
        {
            try
            {
                List<ElementId> oldIds = new FilteredElementCollector(doc, planView.Id)
                    .WhereElementIsNotElementType()
                    .Where(e => e is DetailCurve or TextNote)
                    .Where(e =>
                    {
                        Parameter? p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        string? val = p?.AsString();
                        return val == GostAxisDimTag;
                    })
                    .Select(e => e.Id)
                    .ToList();

                if (oldIds.Count > 0)
                {
                    doc.Delete(oldIds);
                }
            }
            catch { }
        }

        private static void DrawPlanDetailLine(Document doc, ViewPlan planView, XYZ p1, XYZ p2)
        {
            if (p1.DistanceTo(p2) < 0.005) return;
            try
            {
                DetailCurve dc = doc.Create.NewDetailCurve(planView, Line.CreateBound(p1, p2));
                Parameter? p = dc.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (p is { IsReadOnly: false })
                {
                    p.Set(GostAxisDimTag);
                }
            }
            catch { }
        }

        private static void CreatePlanDimText(
            Document doc,
            ViewPlan planView,
            XYZ pos,
            string text,
            ElementId fontTypeId,
            double rotationRad)
        {
            if (fontTypeId == ElementId.InvalidElementId || string.IsNullOrWhiteSpace(text)) return;
            try
            {
                TextNoteOptions opts = new TextNoteOptions
                {
                    TypeId = fontTypeId,
                    HorizontalAlignment = HorizontalTextAlignment.Center,
                    Rotation = rotationRad
                };
                TextNote note = TextNote.Create(doc, planView.Id, pos, text, opts);
                Parameter? p = note.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (p is { IsReadOnly: false })
                {
                    p.Set(GostAxisDimTag);
                }
            }
            catch { }
        }

        private static string FormatMillimetersGost(int mm)
        {
            return mm.ToString(CultureInfo.InvariantCulture);
        }

        private static ElementId GetOrCreatePlanDimTextType(Document doc)
        {
            const string targetName = "ГОСТ_РазмерОсей_2.5мм";
            List<TextNoteType> types = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .ToList();

            TextNoteType? existing = types.FirstOrDefault(t => t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.Id;

            TextNoteType? baseType = types.FirstOrDefault();
            if (baseType == null) return ElementId.InvalidElementId;

            try
            {
                if (baseType.Duplicate(targetName) is TextNoteType created)
                {
                    try { created.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set("ISOCPEUR"); } catch { }
                    try { created.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(2.5 * MmToFeet); } catch { }
                    try { created.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.Set(0.85); } catch { }
                    try { created.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1); } catch { } // Прозрачный фон
                    return created.Id;
                }
            }
            catch { }

            return baseType.Id;
        }
    }
}
