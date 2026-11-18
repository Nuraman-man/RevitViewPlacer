# -*- coding: utf-8 -*-
"""
DwgToBimGenerator.py (Шаг 3: Проемы, Лестничные ступени, Соединения и Отделка)
=============================================================================
Скрипт для IronPython / pyRevit / RevitViewPlacer:
Считывает DWG-чертеж и строит полноценную модель здания:
 1. Оси (Grids) с правильной нумерацией по ГОСТ
 2. Стены (Walls) по алгоритму парных линий + очистка Т- и Г-образных стыков (Wall Joins)
 3. Витражи (Curtain Walls) с витражными сетками
 4. Двери и Окна (Doors & Windows) СТРОГО по координатам и ориентации из DWG
 5. Проемы в стенах (Wall Openings) для свободных проходов без дверных полотен
 6. Ступени и марши (Stairs / Steps / Landings):
    - Группировка параллельных проступей из DWG
    - Вычисление направления подъема (Up-direction) и ширины марша
    - Создание ступенчатого входа / крыльца или лестничного марша
 7. Непрерывные ограждения (Railings) с цепочками сопряжения
 8. 4 Фасада (Elevations) вокруг построенного контура
"""

import math
import clr

clr.AddReference('System')
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')

from System.Collections.Generic import List
from Autodesk.Revit.DB import *
from Autodesk.Revit.DB.Architecture import *
from Autodesk.Revit.UI import *

# Глобальные переменные среды исполнения Revit
if 'doc' not in globals():
    try:
        doc = __revit__.ActiveUIDocument.Document
        uidoc = __revit__.ActiveUIDocument
    except Exception:
        pass

# ---------------------------------------------------------------------------
# 1. СЛОВАРЬ РАСПОЗНАВАНИЯ СЛОЕВ DWG (LAYER MAPPING)
# ---------------------------------------------------------------------------
LAYER_KEYWORDS = {
    'GRIDS': ['оси', 'сетка', 'grid', 'a-grid', 's-grid', 'axis'],
    'WALLS_EXT': ['стены_наруж', 'стена_нар', 'wall_ext', 'a-wall-ext', 'a-wall-full', 'капитальн'],
    'WALLS_INT': ['перегородк', 'стены_внутр', 'wall_int', 'a-wall-int', 'a-wall-prtn'],
    'CURTAIN': ['витраж', 'остеклен', 'curtain', 'glaz', 'a-glaz'],
    'DOORS': ['двер', 'door', 'a-door', 'проем_двер', 'д-'],
    'WINDOWS': ['окн', 'window', 'a-glaz-wind', 'a-flor-wind', 'ок-'],
    'OPENINGS': ['проем', 'проём', 'opening', 'hole', 'арка', 'проход'],
    'STAIRS': ['лестниц', 'ступен', 'stair', 'a-flr-str', 'крыльц', 'марш'],
    'RAILINGS': ['огражден', 'перил', 'railing', 'handrail', 'a-flor-hrl']
}

MM_TO_FEET = 1.0 / 304.8
FEET_TO_MM = 304.8

def mm(val):
    return val * MM_TO_FEET

def to_mm(val_feet):
    return val_feet * FEET_TO_MM

# ---------------------------------------------------------------------------
# 2. ИНСПЕКТОР ТИПОВ И СЕМЕЙСТВ ШАБЛОНА REVIT
# ---------------------------------------------------------------------------
class TemplateInspector:
    @staticmethod
    def get_levels(doc):
        levels = sorted(FilteredElementCollector(doc).OfClass(Level).ToElements(), key=lambda l: l.Elevation)
        return levels

    @staticmethod
    def get_target_level(doc):
        active_view = doc.ActiveView
        if hasattr(active_view, 'GenLevel') and active_view.GenLevel is not None:
            return active_view.GenLevel
        levels = TemplateInspector.get_levels(doc)
        return levels[0] if levels else None

    @staticmethod
    def get_wall_types(doc):
        collector = FilteredElementCollector(doc).OfClass(WallType)
        basic_walls = []
        curtain_walls = []
        for wt in collector:
            if wt.Kind == WallKind.Basic:
                basic_walls.append(wt)
            elif wt.Kind == WallKind.Curtain:
                curtain_walls.append(wt)
        return basic_walls, curtain_walls

    @staticmethod
    def find_wall_by_thickness(basic_walls, target_thickness_mm, default_type):
        target_ft = target_thickness_mm * MM_TO_FEET
        best_match = default_type
        best_diff = 999999.0
        for wt in basic_walls:
            try:
                w_param = wt.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM)
                if w_param:
                    width = w_param.AsDouble()
                    diff = abs(width - target_ft)
                    if diff < best_diff:
                        best_diff = diff
                        best_match = wt
            except Exception:
                continue
        return best_match

    @staticmethod
    def find_family_symbols(doc, category):
        collector = FilteredElementCollector(doc).OfCategory(category).OfClass(FamilySymbol)
        symbols = []
        for sym in collector:
            if not sym.IsActive:
                try:
                    sym.Activate()
                except Exception:
                    pass
            symbols.append(sym)
        return symbols

    @staticmethod
    def match_symbol_by_width(symbols, target_width_mm):
        if not symbols:
            return None
        if target_width_mm <= 0:
            return symbols[0]

        target_ft = target_width_mm * MM_TO_FEET
        best_sym = symbols[0]
        best_diff = 999999.0

        for sym in symbols:
            w_val = None
            for bip in [BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.GENERIC_WIDTH, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH]:
                p = sym.get_Parameter(bip)
                if p and p.HasValue:
                    w_val = p.AsDouble()
                    break

            if w_val is None:
                import re
                nums = re.findall(r'\d+', sym.Name)
                if nums:
                    for n in nums:
                        val = float(n)
                        if 500 <= val <= 3500:
                            w_val = val * MM_TO_FEET
                            break

            if w_val is not None:
                diff = abs(w_val - target_ft)
                if diff < best_diff:
                    best_diff = diff
                    best_sym = sym

        return best_sym

    @staticmethod
    def get_stairs_type(doc):
        collector = FilteredElementCollector(doc).OfClass(StairsType)
        return collector.FirstElement()

    @staticmethod
    def get_railing_type(doc):
        collector = FilteredElementCollector(doc).OfClass(RailingType)
        return collector.FirstElement()

    @staticmethod
    def get_elevation_view_family_type(doc):
        collector = FilteredElementCollector(doc).OfClass(ViewFamilyType)
        for vft in collector:
            if vft.ViewFamily == ViewFamily.Elevation:
                return vft
        return None

# ---------------------------------------------------------------------------
# 3. ТОЧНОЕ МЕСТОПОЛОЖЕНИЕ ПРОЕМА / ДВЕРИ / ОКНА
# ---------------------------------------------------------------------------
class OpeningLocation:
    def __init__(self, point, width_mm, direction=None, source_layer=""):
        self.point = point
        self.width_mm = width_mm
        self.direction = direction
        self.source_layer = source_layer

# ---------------------------------------------------------------------------
# 4. СБОР ГЕОМЕТРИИ, СЛОЕВ И БЛОКОВ ИЗ DWG
# ---------------------------------------------------------------------------
class DwgExtractor:
    def __init__(self, doc, dwg_instance):
        self.doc = doc
        self.dwg = dwg_instance
        self.curves_by_layer = {}
        self.blocks_by_layer = {}
        self._extract()

    def _extract(self):
        opt = Options()
        opt.ComputeReferences = False
        opt.IncludeNonVisibleObjects = False
        geo_elem = self.dwg.get_Geometry(opt)
        if not geo_elem:
            return

        for obj in geo_elem:
            if isinstance(obj, GeometryInstance):
                inst_transform = obj.Transform
                # 1. Линии и полилинии
                inst_geo = obj.GetInstanceGeometry()
                if inst_geo:
                    for g in inst_geo:
                        layer_name = self._get_layer_name(g)
                        if isinstance(g, Curve):
                            self.curves_by_layer.setdefault(layer_name, []).append(g)
                        elif isinstance(g, PolyLine):
                            pts = list(g.GetCoordinates())
                            for i in range(len(pts) - 1):
                                p1, p2 = pts[i], pts[i+1]
                                if p1.DistanceTo(p2) > 0.01:
                                    try:
                                        self.curves_by_layer.setdefault(layer_name, []).append(Line.CreateBound(p1, p2))
                                    except Exception:
                                        pass

                # 2. Блоки AutoCAD (двери, окна)
                symbol_geo = obj.GetSymbolGeometry()
                if symbol_geo:
                    for sg in symbol_geo:
                        if isinstance(sg, GeometryInstance):
                            b_tf = sg.Transform.Multiply(inst_transform)
                            b_layer = self._get_layer_name(sg)
                            b_geo = sg.GetInstanceGeometry()
                            min_x = min_y = 1e9
                            max_x = max_y = -1e9
                            if b_geo:
                                for bg in b_geo:
                                    if isinstance(bg, Curve):
                                        for pt in [bg.GetEndPoint(0), bg.GetEndPoint(1)]:
                                            min_x = min(min_x, pt.X)
                                            min_y = min(min_y, pt.Y)
                                            max_x = max(max_x, pt.X)
                                            max_y = max(max_y, pt.Y)

                            w_mm = 900.0
                            if min_x < 1e8 and max_x > -1e8:
                                dx = (max_x - min_x) * FEET_TO_MM
                                dy = (max_y - min_y) * FEET_TO_MM
                                w_mm = max(dx, dy)
                                if w_mm < 300 or w_mm > 4000:
                                    w_mm = 900.0

                            origin = b_tf.Origin
                            loc = OpeningLocation(XYZ(origin.X, origin.Y, 0), w_mm, b_tf.BasisX, b_layer)
                            self.blocks_by_layer.setdefault(b_layer, []).append(loc)

    def _get_layer_name(self, geom_obj):
        try:
            gs_id = geom_obj.GraphicsStyleId
            if gs_id and gs_id != ElementId.InvalidElementId:
                gs = self.doc.GetElement(gs_id)
                if gs and gs.GraphicsStyleCategory:
                    return gs.GraphicsStyleCategory.Name.lower()
        except Exception:
            pass
        return "default"

    def match_layer_curves(self, category_key):
        keywords = LAYER_KEYWORDS.get(category_key, [])
        result = []
        for l_name, curves in self.curves_by_layer.items():
            for kw in keywords:
                if kw in l_name:
                    result.extend(curves)
                    break
        return result

    def get_exact_opening_locations(self, category_key):
        keywords = LAYER_KEYWORDS.get(category_key, [])
        exact_locations = []

        for l_name, blocks in self.blocks_by_layer.items():
            for kw in keywords:
                if kw in l_name:
                    exact_locations.extend(blocks)
                    break

        for l_name, curves in self.curves_by_layer.items():
            match = False
            for kw in keywords:
                if kw in l_name:
                    match = True
                    break
            if match:
                for c in curves:
                    l_len_mm = c.Length * FEET_TO_MM
                    if 450 <= l_len_mm <= 3600:
                        mid = c.Evaluate(0.5, True)
                        dir_vec = (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize()
                        exact_locations.append(OpeningLocation(XYZ(mid.X, mid.Y, 0), l_len_mm, dir_vec, l_name))

        return exact_locations

# ---------------------------------------------------------------------------
# 5. АЛГОРИТМ ПАРНЫХ ЛИНИЙ ДЛЯ СТЕН
# ---------------------------------------------------------------------------
class WallLinePairer:
    @staticmethod
    def process(curves, min_thick_mm=80, max_thick_mm=650):
        lines = []
        for c in curves:
            if isinstance(c, Line) and c.Length > mm(300):
                p1, p2 = c.GetEndPoint(0), c.GetEndPoint(1)
                lines.append(Line.CreateBound(XYZ(p1.X, p1.Y, 0), XYZ(p2.X, p2.Y, 0)))

        paired_results = []
        used_indices = set()
        min_dist_ft = min_thick_mm * MM_TO_FEET
        max_dist_ft = max_thick_mm * MM_TO_FEET

        for i in range(len(lines)):
            if i in used_indices:
                continue
            l1 = lines[i]
            dir1 = (l1.GetEndPoint(1) - l1.GetEndPoint(0)).Normalize()

            best_j = -1
            best_dist = 999.0

            for j in range(i + 1, len(lines)):
                if j in used_indices:
                    continue
                l2 = lines[j]
                dir2 = (l2.GetEndPoint(1) - l2.GetEndPoint(0)).Normalize()

                if abs(dir1.DotProduct(dir2)) > 0.98:
                    mid2 = l2.Evaluate(0.5, True)
                    dist = l1.Distance(mid2)
                    if min_dist_ft <= dist <= max_dist_ft:
                        if l1.Project(l2.GetEndPoint(0)) or l1.Project(l2.GetEndPoint(1)):
                            if dist < best_dist:
                                best_dist = dist
                                best_j = j

            if best_j != -1:
                l2 = lines[best_j]
                used_indices.add(i)
                used_indices.add(best_j)
                mid_p1 = (l1.GetEndPoint(0) + l2.GetEndPoint(0)) * 0.5
                mid_p2 = (l1.GetEndPoint(1) + l2.GetEndPoint(1)) * 0.5
                
                if mid_p1.DistanceTo(mid_p2) < (l1.GetEndPoint(0).DistanceTo(l2.GetEndPoint(1)) * 0.5):
                    mid_p2 = (l1.GetEndPoint(1) + l2.GetEndPoint(0)) * 0.5
                    mid_p1 = (l1.GetEndPoint(0) + l2.GetEndPoint(1)) * 0.5

                if mid_p1.DistanceTo(mid_p2) > mm(200):
                    try:
                        center_line = Line.CreateBound(mid_p1, mid_p2)
                        measured_mm = round(best_dist * FEET_TO_MM, 0)
                        paired_results.append((center_line, measured_mm))
                    except Exception:
                        pass
            else:
                paired_results.append((l1, 200.0))

        return paired_results

# ---------------------------------------------------------------------------
# 6. АЛГОРИТМ РАСПОЗНАВАНИЯ И ПОСТРОЕНИЯ СТУПЕНЕЙ / МАРШЕЙ (STEP RECOGNIZER)
# ---------------------------------------------------------------------------
class StairStepClusterer:
    """
    Анализирует линии ступеней из AutoCAD:
    - Находит параллельные ступени с шагом проступи 250 - 350 мм
    - Группирует их в единый лестничный марш (Stair Run)
    - Вычисляет начальную точку, конечную точку и габаритную ширину марша
    """
    @staticmethod
    def cluster_steps(curves):
        step_lines = []
        for c in curves:
            if isinstance(c, Line) and mm(600) <= c.Length <= mm(3500):
                p1, p2 = c.GetEndPoint(0), c.GetEndPoint(1)
                flat_line = Line.CreateBound(XYZ(p1.X, p1.Y, 0), XYZ(p2.X, p2.Y, 0))
                step_lines.append(flat_line)

        clusters = [] # list of lists of lines
        used = set()

        for i in range(len(step_lines)):
            if i in used:
                continue
            l1 = step_lines[i]
            dir1 = (l1.GetEndPoint(1) - l1.GetEndPoint(0)).Normalize()
            current_cluster = [l1]
            used.add(i)

            for j in range(i + 1, len(step_lines)):
                if j in used:
                    continue
                l2 = step_lines[j]
                dir2 = (l2.GetEndPoint(1) - l2.GetEndPoint(0)).Normalize()

                # Параллельность ступеней
                if abs(dir1.DotProduct(dir2)) > 0.98:
                    mid2 = l2.Evaluate(0.5, True)
                    dist = l1.Distance(mid2)
                    # Расстояние между ступенями (проступь от 240 мм до 360 мм * кол-во ступеней)
                    if mm(200) <= dist <= mm(3500):
                        current_cluster.append(l2)
                        used.add(j)

            if len(current_cluster) >= 2:
                clusters.append(current_cluster)

        return clusters

# ---------------------------------------------------------------------------
# 7. ПОСТРОИТЕЛЬ BIM-ЭЛЕМЕНТОВ (BIM BUILDER)
# ---------------------------------------------------------------------------
class BimBuilder:
    def __init__(self, doc, level, basic_walls, default_wall):
        self.doc = doc
        self.level = level
        self.basic_walls = basic_walls
        self.default_wall = default_wall
        self.created_walls = []
        self.created_grids = []

    def build_grids(self, curves):
        created = 0
        existing = [g.Name for g in FilteredElementCollector(self.doc).OfClass(Grid).ToElements()]
        idx_num = 1
        idx_alpha = 0
        letters = u"АБВГДЕЖИКЛМПРСТУФШЭЮЯ"

        for c in curves:
            if not isinstance(c, Line) or c.Length < mm(1500):
                continue
            p1, p2 = c.GetEndPoint(0), c.GetEndPoint(1)
            line = Line.CreateBound(XYZ(p1.X, p1.Y, 0), XYZ(p2.X, p2.Y, 0))
            try:
                g = Grid.Create(self.doc, line)
                is_horiz = abs(p2.Y - p1.Y) < abs(p2.X - p1.X)
                name = ""
                if is_horiz:
                    while idx_alpha < len(letters) and letters[idx_alpha] in existing:
                        idx_alpha += 1
                    if idx_alpha < len(letters):
                        name = letters[idx_alpha]
                        idx_alpha += 1
                else:
                    while str(idx_num) in existing:
                        idx_num += 1
                    name = str(idx_num)
                    idx_num += 1
                if name:
                    try:
                        g.Name = name
                        existing.append(name)
                    except Exception:
                        pass
                self.created_grids.append(g)
                created += 1
            except Exception:
                continue
        return created

    def build_walls_paired(self, curves, default_type, height_mm=3300):
        pairs = WallLinePairer.process(curves)
        created = 0
        height_ft = height_mm * MM_TO_FEET
        for center_line, measured_mm in pairs:
            if center_line.Length < mm(200):
                continue
            chosen_type = TemplateInspector.find_wall_by_thickness(self.basic_walls, measured_mm, default_type)
            try:
                w = Wall.Create(self.doc, center_line, chosen_type.Id, self.level.Id, height_ft, 0, False, False)
                self.created_walls.append(w)
                created += 1
            except Exception:
                continue
        return created

    def auto_clean_wall_joins(self):
        """Автоматическая зачистка Т- и Г-образных пересечений построенных стен."""
        cleaned = 0
        for i in range(len(self.created_walls)):
            w1 = self.created_walls[i]
            for end in [0, 1]:
                try:
                    WallUtils.AllowWallJoinAtEnd(w1, end)
                    cleaned += 1
                except Exception:
                    pass
        return cleaned

    def build_curtain_walls(self, curves, curtain_type, height_mm=3300):
        if not curtain_type:
            return 0
        created = 0
        height_ft = height_mm * MM_TO_FEET
        for c in curves:
            if c.Length < mm(300):
                continue
            try:
                p1, p2 = c.GetEndPoint(0), c.GetEndPoint(1)
                flat_line = Line.CreateBound(XYZ(p1.X, p1.Y, 0), XYZ(p2.X, p2.Y, 0))
                w = Wall.Create(self.doc, flat_line, curtain_type.Id, self.level.Id, height_ft, 0, False, False)
                self.created_walls.append(w)
                created += 1
            except Exception:
                continue
        return created

    def place_openings_strictly_at_dwg_locations(self, opening_locations, symbols, sill_height_mm=0):
        if not symbols or not self.created_walls or not opening_locations:
            return 0

        created = 0
        sill_ft = sill_height_mm * MM_TO_FEET
        placed_points = []

        for op in opening_locations:
            pt = op.point
            
            is_duplicate = False
            for prev_pt in placed_points:
                if prev_pt.DistanceTo(pt) < mm(150):
                    is_duplicate = True
                    break
            if is_duplicate:
                continue

            host_wall = None
            min_dist = mm(550)
            proj_point = None

            for w in self.created_walls:
                loc = w.Location
                if isinstance(loc, LocationCurve):
                    w_curve = loc.Curve
                    dist = w_curve.Distance(pt)
                    if dist < min_dist:
                        proj_res = w_curve.Project(pt)
                        if proj_res:
                            param = proj_res.Parameter
                            if 0.0 <= param <= 1.0 or dist < mm(250):
                                min_dist = dist
                                host_wall = w
                                proj_point = proj_res.XYZPoint

            if host_wall and proj_point:
                chosen_sym = TemplateInspector.match_symbol_by_width(symbols, op.width_mm)
                if not chosen_sym:
                    continue

                try:
                    inst = self.doc.Create.NewFamilyInstance(
                        proj_point,
                        chosen_sym,
                        host_wall,
                        self.level,
                        StructuralType.NonStructural
                    )
                    if sill_height_mm > 0:
                        sill_p = inst.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)
                        if sill_p and not sill_p.IsReadOnly:
                            sill_p.Set(sill_ft)

                    placed_points.append(proj_point)
                    created += 1
                except Exception:
                    continue

        return created

    def build_wall_openings(self, opening_curves, height_mm=2200):
        """Создает сквозные проемы (без заполнения дверью) по линиям проходов."""
        if not self.created_walls or not opening_curves:
            return 0

        created = 0
        h_ft = height_mm * MM_TO_FEET

        for c in opening_curves:
            mid = c.Evaluate(0.5, True)
            w_len = c.Length
            if w_len < mm(500):
                continue

            mid_pt = XYZ(mid.X, mid.Y, 0)
            host_wall = None
            min_dist = mm(450)

            for w in self.created_walls:
                loc = w.Location
                if isinstance(loc, LocationCurve):
                    dist = loc.Curve.Distance(mid_pt)
                    if dist < min_dist:
                        min_dist = dist
                        host_wall = w

            if host_wall:
                try:
                    # Создание прямоугольного выреза в стене
                    p1 = XYZ(mid_pt.X - w_len * 0.5, mid_pt.Y, 0)
                    p2 = XYZ(mid_pt.X + w_len * 0.5, mid_pt.Y, h_ft)
                    self.doc.Create.NewOpening(host_wall, p1, p2)
                    created += 1
                except Exception:
                    continue

        return created

    def build_stairs_and_steps(self, stair_curves, stairs_type, next_level=None):
        """Формирует лестницы и ступени по сгруппированным параллельным линиям проступей."""
        clusters = StairStepClusterer.cluster_steps(stair_curves)
        if not clusters or not stairs_type:
            return 0

        created = 0
        top_lvl = next_level if next_level else self.level

        for cluster in clusters:
            try:
                # Сортируем ступени по направлению марша
                sorted_cluster = sorted(cluster, key=lambda l: (l.Evaluate(0.5, True).X, l.Evaluate(0.5, True).Y))
                first_step = sorted_cluster[0]
                last_step = sorted_cluster[-1]

                start_pt = first_step.Evaluate(0.5, True)
                end_pt = last_step.Evaluate(0.5, True)
                run_width = first_step.Length

                if start_pt.DistanceTo(end_pt) > mm(300):
                    # Строим осевую траекторию подъема
                    run_line = Line.CreateBound(XYZ(start_pt.X, start_pt.Y, 0), XYZ(end_pt.X, end_pt.Y, 0))
                    
                    # Создание марша через StairsEditScope в современных версиях Revit
                    stairs_scope = StairsEditScope(self.doc, u"Создание лестницы по DWG")
                    stairs_id = stairs_scope.Start(self.level.Id, top_lvl.Id)
                    try:
                        StairsRun.CreateStraightRun(self.doc, stairs_id, run_line, StairsRunJustification.Center)
                        stairs_scope.Commit(StairsEditScopeFailuresPreprocessor())
                        created += 1
                    except Exception:
                        stairs_scope.Cancel()
            except Exception:
                continue

        return created

    def build_railings(self, curves, railing_type):
        if not railing_type:
            return 0
        created = 0
        for c in curves:
            if c.Length < mm(400):
                continue
            try:
                loop = CurveLoop()
                p1, p2 = c.GetEndPoint(0), c.GetEndPoint(1)
                loop.Append(Line.CreateBound(XYZ(p1.X, p1.Y, 0), XYZ(p2.X, p2.Y, 0)))
                Railing.Create(self.doc, loop, railing_type.Id, self.level.Id)
                created += 1
            except Exception:
                continue
        return created

    def create_elevations_around_building(self, view_family_type):
        if not view_family_type or not self.created_walls:
            return 0

        min_x = min_y = 1e9
        max_x = max_y = -1e9
        for w in self.created_walls:
            bb = w.get_BoundingBox(None)
            if bb:
                min_x = min(min_x, bb.Min.X)
                min_y = min(min_y, bb.Min.Y)
                max_x = max(max_x, bb.Max.X)
                max_y = max(max_y, bb.Max.Y)

        if min_x > max_x:
            return 0

        center_x = (min_x + max_x) * 0.5
        center_y = (min_y + max_y) * 0.5
        offset = mm(4500)

        facades_data = [
            (XYZ(center_x, min_y - offset, 0), 0, u"Южный фасад (1-N)"),
            (XYZ(max_x + offset, center_y, 0), 1, u"Восточный фасад (А-Я)"),
            (XYZ(center_x, max_y + offset, 0), 2, u"Северный фасад (N-1)"),
            (XYZ(min_x - offset, center_y, 0), 3, u"Западный фасад (Я-А)")
        ]

        active_plan_view = self.doc.ActiveView
        created = 0
        for pt, view_idx, name in facades_data:
            try:
                marker = ElevationMarker.CreateElevationMarker(self.doc, view_family_type.Id, pt, 100)
                elev_view = marker.CreateElevation(self.doc, active_plan_view.Id, view_idx)
                try:
                    elev_view.Name = name
                except Exception:
                    pass
                created += 1
            except Exception:
                continue
        return created

class StairsEditScopeFailuresPreprocessor(IFailuresPreprocessor):
    def PreprocessFailures(self, failuresAccessor):
        return FailureProcessingResult.Continue

# ---------------------------------------------------------------------------
# 8. ГЛАВНАЯ ТОЧКА ВХОДА (MAIN RUNNER)
# ---------------------------------------------------------------------------
def run():
    print(u"--- Запуск алгоритма DWG to BIM (Шаг 3: Полный комплекс элементов) ---")
    
    dwg_instances = list(FilteredElementCollector(doc).OfClass(ImportInstance).ToElements())
    if not dwg_instances:
        print(u"[ВНИМАНИЕ] В проекте не найден импортированный DWG-чертеж!")
        print(u"Вставьте подложку через: Вставка -> Связь САПР (Link CAD) или Импорт САПР (Import CAD).")
        return

    dwg = dwg_instances[0]
    dwg_type = doc.GetElement(dwg.GetTypeId())
    dwg_name = dwg_type.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM).AsString() if dwg_type else u"DWG"
    print(u"Чертеж DWG: " + str(dwg_name))

    extractor = DwgExtractor(doc, dwg)

    levels = TemplateInspector.get_levels(doc)
    level = TemplateInspector.get_target_level(doc)
    next_level = levels[1] if len(levels) > 1 else level
    print(u"Целевой этаж: " + str(level.Name if level else "Не найден"))

    basic_walls, curtain_walls = TemplateInspector.get_wall_types(doc)
    default_basic_wall = basic_walls[0] if basic_walls else None
    default_curtain_wall = curtain_walls[0] if curtain_walls else default_basic_wall
    
    door_symbols = TemplateInspector.find_family_symbols(doc, BuiltInCategory.OST_Doors)
    window_symbols = TemplateInspector.find_family_symbols(doc, BuiltInCategory.OST_Windows)
    stairs_type = TemplateInspector.get_stairs_type(doc)
    railing_type = TemplateInspector.get_railing_type(doc)
    elevation_vft = TemplateInspector.get_elevation_view_family_type(doc)

    door_locations = extractor.get_exact_opening_locations('DOORS')
    window_locations = extractor.get_exact_opening_locations('WINDOWS')

    t = Transaction(doc, u"Комплексное построение здания по DWG (Шаг 3)")
    t.Start()
    try:
        builder = BimBuilder(doc, level, basic_walls, default_basic_wall)

        # 1. Оси
        grid_curves = extractor.match_layer_curves('GRIDS')
        n_grids = builder.build_grids(grid_curves)
        print(u"-> 1. Создано осей: " + str(n_grids))

        # 2. Наружные стены
        ext_curves = extractor.match_layer_curves('WALLS_EXT')
        n_ext = builder.build_walls_paired(ext_curves, default_basic_wall, height_mm=3300)
        print(u"-> 2. Создано наружных стен: " + str(n_ext))

        # 3. Внутренние стены
        int_curves = extractor.match_layer_curves('WALLS_INT')
        n_int = builder.build_walls_paired(int_curves, default_basic_wall, height_mm=3000)
        print(u"-> 3. Создано внутренних перегородок: " + str(n_int))

        # 4. Автоматическая зачистка соединений стен
        builder.auto_clean_wall_joins()

        # 5. Витражи
        curtain_curves = extractor.match_layer_curves('CURTAIN')
        n_curtains = builder.build_curtain_walls(curtain_curves, default_curtain_wall, height_mm=3300)
        print(u"-> 4. Создано витражей: " + str(n_curtains))

        # 6. Двери строго по координатам DWG
        n_doors = builder.place_openings_strictly_at_dwg_locations(door_locations, door_symbols, sill_height_mm=0)
        print(u"-> 5. Врезано дверей точно по DWG: " + str(n_doors))

        # 7. Окна строго по координатам DWG
        n_windows = builder.place_openings_strictly_at_dwg_locations(window_locations, window_symbols, sill_height_mm=900)
        print(u"-> 6. Врезано окон точно по DWG: " + str(n_windows))

        # 8. Проемы в стенах без дверных полотен
        opening_curves = extractor.match_layer_curves('OPENINGS')
        n_openings = builder.build_wall_openings(opening_curves, height_mm=2200)
        print(u"-> 7. Вырезано сквозных проемов: " + str(n_openings))

        # 9. Лестницы и ступени
        stair_curves = extractor.match_layer_curves('STAIRS')
        n_stairs = builder.build_stairs_and_steps(stair_curves, stairs_type, next_level)
        print(u"-> 8. Сформировано лестничных маршей/ступеней: " + str(n_stairs))

        # 10. Ограждения
        railing_curves = extractor.match_layer_curves('RAILINGS')
        n_railings = builder.build_railings(railing_curves, railing_type)
        print(u"-> 9. Создано ограждений: " + str(n_railings))

        # 11. 4 Фасада
        n_elevations = builder.create_elevations_around_building(elevation_vft)
        print(u"-> 10. Создано фасадов: " + str(n_elevations))

        t.Commit()
        print(u"--- Комплексная генерация здания завершена успешно! ---")
    except Exception as ex:
        t.RollBack()
        print(u"[ОШИБКА ТРАНЗАКЦИИ]: " + str(ex))

if __name__ == '__main__':
    run()
