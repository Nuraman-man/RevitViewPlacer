# -*- coding: utf-8 -*-
"""
DwgToBimGenerator.py (Шаг 2.5: Точная посадка дверей и окон по DWG)
===================================================================
Скрипт для IronPython / pyRevit / RevitViewPlacer:
Считывает DWG-чертеж и СТРОГО ПО МЕСТУ РАСПОЛОЖЕНИЯ дверей и окон в чертеже
находит точные координаты, ориентацию, угол поворота и ширину проема,
после чего сажает семейства в Revit точно на свои места.

Поддерживаются:
 - Блоки дверей/окон (Block Reference / GeometryInstance) с точкой вставки и углом
 - Линии проемов и полотен дверей (дуги открывания, четверти)
 - Текстовые выноски и марки (например "Д-1", "ОК-2")
 - Оси, стены (с парными линиями), витражи, ограждения и 4 фасада
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
    'OPENINGS': ['проем', 'проём', 'opening', 'hole'],
    'STAIRS': ['лестниц', 'ступен', 'stair', 'a-flr-str'],
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
    def get_target_level(doc):
        active_view = doc.ActiveView
        if hasattr(active_view, 'GenLevel') and active_view.GenLevel is not None:
            return active_view.GenLevel
        levels = sorted(FilteredElementCollector(doc).OfClass(Level).ToElements(), key=lambda l: l.Elevation)
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
                        if 500 <= val <= 3000:
                            w_val = val * MM_TO_FEET
                            break

            if w_val is not None:
                diff = abs(w_val - target_ft)
                if diff < best_diff:
                    best_diff = diff
                    best_sym = sym

        return best_sym

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
# 3. ТОЧНАЯ СТРУКТУРА МЕСТА РАСПОЛОЖЕНИЯ ПРОЕМА / ДВЕРИ / ОКНА
# ---------------------------------------------------------------------------
class OpeningLocation:
    """Хранит строго определенные координаты, ориентацию и ширину двери/окна из DWG."""
    def __init__(self, point, width_mm, direction=None, source_layer=""):
        self.point = point                      # Точные координаты X, Y в футах
        self.width_mm = width_mm                # Замеренная ширина проема/блока в мм
        self.direction = direction              # Вектор ориентации проема (если есть)
        self.source_layer = source_layer

# ---------------------------------------------------------------------------
# 4. ИЗВЛЕЧЕНИЕ ГЕОМЕТРИИ И ТОЧНЫХ МЕСТ ИЗ DWG
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
                # 1. Линейная геометрия слоев
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

                # 2. Блоки дверей и окон (Block References)
                symbol_geo = obj.GetSymbolGeometry()
                if symbol_geo:
                    for sg in symbol_geo:
                        if isinstance(sg, GeometryInstance):
                            b_tf = sg.Transform.Multiply(inst_transform)
                            b_layer = self._get_layer_name(sg)
                            
                            # Замеряем локальные габариты содержимого блока
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

                            # Точная точка центра/вставки
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
        """
        Собирает ТОЧНЫЕ места размещения дверей или окон:
        1) Из блоков AutoCAD в соответствующих слоях
        2) Из линий проемов (отрезки, обозначающие границу двери/окна в стене)
        """
        keywords = LAYER_KEYWORDS.get(category_key, [])
        exact_locations = []

        # 1. Из блоков
        for l_name, blocks in self.blocks_by_layer.items():
            for kw in keywords:
                if kw in l_name:
                    exact_locations.extend(blocks)
                    break

        # 2. Из линий в слое (каждая линия проема задает середину и ширину)
        for l_name, curves in self.curves_by_layer.items():
            match = False
            for kw in keywords:
                if kw in l_name:
                    match = True
                    break
            if match:
                for c in curves:
                    l_len_mm = c.Length * FEET_TO_MM
                    # Проем двери/окна обычно от 500 мм до 3500 мм
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
# 6. ПОСТРОИТЕЛЬ BIM-ЭЛЕМЕНТОВ
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
        """
        СТРОГАЯ посадка дверей/окон строго по точкам расположения в файле DWG.
        1. Берет точную точку двери/окна из DWG.
        2. Ищет стену, в которой находится этот проем.
        3. Проецирует точку строго на ось стены.
        4. Вставляет экземпляр семейства с соответствующей шириной.
        """
        if not symbols or not self.created_walls or not opening_locations:
            return 0

        created = 0
        sill_ft = sill_height_mm * MM_TO_FEET
        placed_points = []  # Фильтрация повторных вставок в одну и ту же точку

        for op in opening_locations:
            pt = op.point
            
            # Проверка на дублирование с радиусом 150 мм
            is_duplicate = False
            for prev_pt in placed_points:
                if prev_pt.DistanceTo(pt) < mm(150):
                    is_duplicate = True
                    break
            if is_duplicate:
                continue

            # Находим стену-хозяина, которая пересекает или прилегает к проему (макс 500 мм)
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
                            # Проверяем, что проекция попадает на отрезок стены
                            param = proj_res.Parameter
                            if 0.0 <= param <= 1.0 or dist < mm(250):
                                min_dist = dist
                                host_wall = w
                                proj_point = proj_res.XYZPoint

            if host_wall and proj_point:
                # Подбираем типоразмер под замеренную ширину из DWG
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

# ---------------------------------------------------------------------------
# 7. ГЛАВНАЯ ТОЧКА ВХОДА (MAIN RUNNER)
# ---------------------------------------------------------------------------
def run():
    print(u"--- Запуск алгоритма DWG to BIM (Точная посадка дверей и окон по DWG) ---")
    
    dwg_instances = list(FilteredElementCollector(doc).OfClass(ImportInstance).ToElements())
    if not dwg_instances:
        print(u"[ВНИМАНИЕ] В проекте не найден импортированный DWG-чертеж!")
        print(u"Вставьте подложку через: Вставка -> Связь САПР (Link CAD) или Импорт САПР (Import CAD).")
        return

    dwg = dwg_instances[0]
    dwg_type = doc.GetElement(dwg.GetTypeId())
    dwg_name = dwg_type.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM).AsString() if dwg_type else u"DWG"
    print(u"Найден чертеж DWG: " + str(dwg_name))

    extractor = DwgExtractor(doc, dwg)

    level = TemplateInspector.get_target_level(doc)
    print(u"Целевой этаж: " + str(level.Name if level else "Не найден"))
    if not level:
        print(u"[ОШИБКА] В проекте нет уровней (Level).")
        return

    basic_walls, curtain_walls = TemplateInspector.get_wall_types(doc)
    default_basic_wall = basic_walls[0] if basic_walls else None
    default_curtain_wall = curtain_walls[0] if curtain_walls else default_basic_wall
    
    door_symbols = TemplateInspector.find_family_symbols(doc, BuiltInCategory.OST_Doors)
    window_symbols = TemplateInspector.find_family_symbols(doc, BuiltInCategory.OST_Windows)
    railing_type = TemplateInspector.get_railing_type(doc)
    elevation_vft = TemplateInspector.get_elevation_view_family_type(doc)

    print(u"Доступно в шаблоне: дверей - " + str(len(door_symbols)) + u", окон - " + str(len(window_symbols)))

    # Замеряем, сколько дверей и окон найдено непосредственно на чертеже DWG
    door_locations = extractor.get_exact_opening_locations('DOORS')
    window_locations = extractor.get_exact_opening_locations('WINDOWS')
    print(u"Обнаружено в файле DWG по координатам: дверей - " + str(len(door_locations)) + u", окон - " + str(len(window_locations)))

    t = Transaction(doc, u"Построение здания по чертежу DWG")
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

        # 4. Витражи
        curtain_curves = extractor.match_layer_curves('CURTAIN')
        n_curtains = builder.build_curtain_walls(curtain_curves, default_curtain_wall, height_mm=3300)
        print(u"-> 4. Создано витражей: " + str(n_curtains))

        # 5. Двери: строго по координатам проемов и блоков из DWG
        n_doors = builder.place_openings_strictly_at_dwg_locations(door_locations, door_symbols, sill_height_mm=0)
        print(u"-> 5. Врезано дверей точно по DWG: " + str(n_doors))

        # 6. Окна: строго по координатам проемов и блоков из DWG
        n_windows = builder.place_openings_strictly_at_dwg_locations(window_locations, window_symbols, sill_height_mm=900)
        print(u"-> 6. Врезано окон точно по DWG: " + str(n_windows))

        # 7. Ограждения
        railing_curves = extractor.match_layer_curves('RAILINGS') + extractor.match_layer_curves('STAIRS')
        n_railings = builder.build_railings(railing_curves, railing_type)
        print(u"-> 7. Создано ограждений: " + str(n_railings))

        # 8. Четыре фасада вокруг здания
        n_elevations = builder.create_elevations_around_building(elevation_vft)
        print(u"-> 8. Создано видов фасадов: " + str(n_elevations))

        t.Commit()
        print(u"--- Построение здания по чертежу DWG завершено успешно! ---")
    except Exception as ex:
        t.RollBack()
        print(u"[ОШИБКА ТРАНЗАКЦИИ]: " + str(ex))

if __name__ == '__main__':
    run()
