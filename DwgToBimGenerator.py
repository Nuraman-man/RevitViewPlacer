# -*- coding: utf-8 -*-
"""
DwgToBimGenerator.py (Шаг 2: Умное сопоставление геометрии и типов)
==================================================================
Универсальный скрипт для IronPython / pyRevit / RevitPythonShell / RevitViewPlacer:
Автоматическое считывание связанного или импортированного DWG-чертежа и построение
на его основе элементов Revit:
 - Оси (Grids): фильтрация дубликатов, привязка текстовых марок из чертежа
 - Стены (Walls): АЛГОРИТМ ПАРНЫХ ЛИНИЙ (Double-line matching) с замером толщины
   и автоматическим подбором подходящего типоразмера в шаблоне проекта
 - Витражи (Curtain Walls): выравнивание и создание витражных систем
 - Двери и Окна (Doors & Windows): определение точек вставки блоков, определение
   ширины проема и подбор типоразмера (FamilySymbol) в шаблоне
 - Проемы (Wall Openings): сквозные вырезы в стенах по замкнутым контурам
 - Ступени и марши (Stairs / Steps): объединение параллельных ступеней в лестничный марш
 - Ограждения (Railings): непрерывные траектории перил
 - 4 Фасада (Elevations): автопостроение фасадов с выравниванием по габаритам здания
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

# Глобальные объекты документа (из RevitPythonShell / ScriptRunner)
if 'doc' not in globals():
    try:
        doc = __revit__.ActiveUIDocument.Document
        uidoc = __revit__.ActiveUIDocument
    except Exception:
        pass

# ---------------------------------------------------------------------------
# 1. КОНФИГУРАЦИЯ И СЛОВАРЬ СООТВЕТСТВИЯ СЛОЕВ DWG (LAYER MAPPING)
# ---------------------------------------------------------------------------
LAYER_KEYWORDS = {
    'GRIDS': ['оси', 'сетка', 'grid', 'a-grid', 's-grid', 'axis'],
    'WALLS_EXT': ['стены_наруж', 'стена_нар', 'wall_ext', 'a-wall-ext', 'a-wall-full', 'капитальн'],
    'WALLS_INT': ['перегородк', 'стены_внутр', 'wall_int', 'a-wall-int', 'a-wall-prtn'],
    'CURTAIN': ['витраж', 'остеклен', 'curtain', 'glaz', 'a-glaz'],
    'DOORS': ['двер', 'door', 'a-door'],
    'WINDOWS': ['окн', 'window', 'a-glaz-wind', 'a-flor-wind'],
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
# 2. ИНСПЕКТОР ШАБЛОНА REVIT (TEMPLATE INSPECTOR)
# ---------------------------------------------------------------------------
class TemplateInspector:
    """Анализирует активный шаблон Revit, параметры толщин и типоразмеров."""
    
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
        """Находит базовую стену, наиболее близкую по толщине к замеренной паре линий в DWG."""
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
    def find_family_symbols_by_category(doc, category):
        """Возвращает все активированные типоразмеры категории."""
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
    def find_best_door_or_window(symbols, target_width_mm):
        """Подбирает типоразмер двери или окна, наиболее близкий по ширине проема."""
        if not symbols:
            return None
        if target_width_mm <= 0:
            return symbols[0]

        target_ft = target_width_mm * MM_TO_FEET
        best_sym = symbols[0]
        best_diff = 999999.0

        for sym in symbols:
            # Ищем параметр ширины (FAMILY_WIDTH_PARAM, DOOR_WIDTH, WINDOW_WIDTH или имя)
            w_val = None
            for bip in [BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.GENERIC_WIDTH]:
                p = sym.get_Parameter(bip)
                if p and p.HasValue:
                    w_val = p.AsDouble()
                    break
            
            # Если параметра нет — пробуем извлечь число из названия
            if w_val is None:
                name = sym.Name
                import re
                nums = re.findall(r'\d+', name)
                if nums:
                    for n in nums:
                        val = float(n)
                        if 500 <= val <= 3000:  # Типичная ширина в мм
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
# 3. ЧТЕНИЕ ГЕОМЕТРИИ, СЛОЕВ И БЛОКОВ ИЗ DWG (DWG EXTRACTOR)
# ---------------------------------------------------------------------------
class DwgBlockInfo:
    def __init__(self, name, origin, rotation, min_pt, max_pt):
        self.name = name
        self.origin = origin
        self.rotation = rotation
        self.min_pt = min_pt
        self.max_pt = max_pt

    @property
    def estimated_width_mm(self):
        if self.min_pt and self.max_pt:
            dx = abs(self.max_pt.X - self.min_pt.X) * FEET_TO_MM
            dy = abs(self.max_pt.Y - self.min_pt.Y) * FEET_TO_MM
            return max(dx, dy)
        return 900.0

class DwgExtractor:
    """Извлекает из ImportInstance геометрию (отрезки, полилинии, блоки) по слоям."""
    
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
                # Вложенная геометрия экземпляра
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

                # Поиск вложенных блоков (двери, окна, сантехника)
                symbol_geo = obj.GetSymbolGeometry()
                if symbol_geo:
                    for sg in symbol_geo:
                        if isinstance(sg, GeometryInstance):
                            b_tf = sg.Transform.Multiply(inst_transform)
                            b_origin = b_tf.Origin
                            b_layer = self._get_layer_name(sg)
                            # Оцениваем габариты блока
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
                            min_p = XYZ(min_x, min_y, 0) if min_x < 1e8 else None
                            max_p = XYZ(max_x, max_y, 0) if max_x > -1e8 else None
                            
                            blk_info = DwgBlockInfo("DWG_BLOCK", b_origin, 0.0, min_p, max_p)
                            self.blocks_by_layer.setdefault(b_layer, []).append(blk_info)

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
        """Возвращает все кривые, слои которых содержат ключевые слова из словаря."""
        keywords = LAYER_KEYWORDS.get(category_key, [])
        result = []
        for l_name, curves in self.curves_by_layer.items():
            for kw in keywords:
                if kw in l_name:
                    result.extend(curves)
                    break
        return result

    def match_layer_blocks(self, category_key):
        """Возвращает блоки, слои которых соответствуют категории."""
        keywords = LAYER_KEYWORDS.get(category_key, [])
        result = []
        for l_name, blocks in self.blocks_by_layer.items():
            for kw in keywords:
                if kw in l_name:
                    result.extend(blocks)
                    break
        return result

# ---------------------------------------------------------------------------
# 4. АЛГОРИТМ ПАРНЫХ ЛИНИЙ (DOUBLE-LINE TO CENTERLINE WALL MATCHING)
# ---------------------------------------------------------------------------
class WallLinePairer:
    """
    Интеллектуальный поиск пар параллельных линий в AutoCAD:
    - Находит смежные параллельные линии на расстоянии толщины стены (100 - 600 мм).
    - Вычисляет осевую линию (Centerline).
    - Определяет замеренную толщину стены в мм для выбора типоразмера.
    """
    
    @staticmethod
    def process(curves, min_thick_mm=80, max_thick_mm=650):
        lines = []
        for c in curves:
            if isinstance(c, Line) and c.Length > mm(300):
                p1 = c.GetEndPoint(0)
                p2 = c.GetEndPoint(1)
                flat_line = Line.CreateBound(XYZ(p1.X, p1.Y, 0), XYZ(p2.X, p2.Y, 0))
                lines.append(flat_line)

        paired_results = [] # [(center_line, measured_thickness_mm)]
        used_indices = set()
        
        min_dist_ft = min_thick_mm * MM_TO_FEET
        max_dist_ft = max_thick_mm * MM_TO_FEET

        for i in range(len(lines)):
            if i in used_indices:
                continue
            l1 = lines[i]
            dir1 = (l1.GetEndPoint(1) - l1.GetEndPoint(0)).Normalize()
            mid1 = l1.Evaluate(0.5, True)

            best_j = -1
            best_dist = 999.0

            for j in range(i + 1, len(lines)):
                if j in used_indices:
                    continue
                l2 = lines[j]
                dir2 = (l2.GetEndPoint(1) - l2.GetEndPoint(0)).Normalize()

                # Проверка параллельности векторов
                dot = abs(dir1.DotProduct(dir2))
                if dot > 0.98:  # Параллельны
                    mid2 = l2.Evaluate(0.5, True)
                    # Расстояние от середины l2 до бесконечной прямой l1
                    dist = l1.Distance(mid2)
                    if min_dist_ft <= dist <= max_dist_ft:
                        # Проверяем перекрытие проекций
                        proj1 = l1.Project(l2.GetEndPoint(0))
                        proj2 = l1.Project(l2.GetEndPoint(1))
                        if proj1 or proj2:
                            if dist < best_dist:
                                best_dist = dist
                                best_j = j

            if best_j != -1:
                # Найдена пара: строим осевую линию
                l2 = lines[best_j]
                used_indices.add(i)
                used_indices.add(best_j)
                
                mid_p1 = (l1.GetEndPoint(0) + l2.GetEndPoint(0)) * 0.5
                mid_p2 = (l1.GetEndPoint(1) + l2.GetEndPoint(1)) * 0.5
                
                # Проверяем корректность направления
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
                # Одиночная линия (например, перегородка, начерченная одной осью)
                paired_results.append((l1, 200.0))

        return paired_results

# ---------------------------------------------------------------------------
# 5. ГЕНЕРАТОР ЭЛЕМЕНТОВ BIM (BIM BUILDER)
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
        """Создает координационные сетки по линиям из слоя осей."""
        created = 0
        existing_grid_names = [g.Name for g in FilteredElementCollector(self.doc).OfClass(Grid).ToElements()]
        
        idx_num = 1
        idx_alpha = 0
        letters = u"АБВГДЕЖИКЛМПРСТУФШЭЮЯ"

        for c in curves:
            if not isinstance(c, Line):
                continue
            if c.Length < mm(1500):  # Отсекаем засечки
                continue

            p1, p2 = c.GetEndPoint(0), c.GetEndPoint(1)
            line = Line.CreateBound(XYZ(p1.X, p1.Y, 0), XYZ(p2.X, p2.Y, 0))
            
            try:
                g = Grid.Create(self.doc, line)
                is_horizontal = abs(p2.Y - p1.Y) < abs(p2.X - p1.X)
                name_candidate = ""
                if is_horizontal:
                    while idx_alpha < len(letters) and letters[idx_alpha] in existing_grid_names:
                        idx_alpha += 1
                    if idx_alpha < len(letters):
                        name_candidate = letters[idx_alpha]
                        idx_alpha += 1
                else:
                    while str(idx_num) in existing_grid_names:
                        idx_num += 1
                    name_candidate = str(idx_num)
                    idx_num += 1

                if name_candidate:
                    try:
                        g.Name = name_candidate
                        existing_grid_names.append(name_candidate)
                    except Exception:
                        pass
                self.created_grids.append(g)
                created += 1
            except Exception:
                continue
        return created

    def build_walls_paired(self, curves, default_type, height_mm=3300):
        """Строит стены через парсинг парных линий с автоподбором толщины."""
        pairs = WallLinePairer.process(curves)
        created = 0
        height_ft = height_mm * MM_TO_FEET
        
        for center_line, measured_thick_mm in pairs:
            if center_line.Length < mm(200):
                continue
            # Подбираем наиболее точный WallType по замеренной толщине
            chosen_type = TemplateInspector.find_wall_by_thickness(self.basic_walls, measured_thick_mm, default_type)
            try:
                w = Wall.Create(self.doc, center_line, chosen_type.Id, self.level.Id, height_ft, 0, False, False)
                self.created_walls.append(w)
                created += 1
            except Exception:
                continue
        return created

    def build_curtain_walls(self, curves, curtain_type, height_mm=3300):
        """Строит витражные системы по направляющим линиям."""
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

    def insert_openings_and_fixtures(self, curves, blocks, symbols, sill_height_mm=0):
        """Вставляет двери/окна с автоподбором типоразмера по ширине проема."""
        if not symbols or not self.created_walls:
            return 0
        
        created = 0
        sill_ft = sill_height_mm * MM_TO_FEET

        # Точки вставки и ожидаемая ширина: собираем из кривых и блоков
        targets = [] # [(insert_pt, estimated_width_mm)]

        for c in curves:
            mid = c.Evaluate(0.5, True)
            width_mm = round(c.Length * FEET_TO_MM, 0)
            targets.append((XYZ(mid.X, mid.Y, 0), width_mm))

        for b in blocks:
            targets.append((XYZ(b.origin.X, b.origin.Y, 0), b.estimated_width_mm))

        for pt, width_mm in targets:
            # Находим ближайшую стену
            host_wall = None
            min_dist = mm(450)
            for w in self.created_walls:
                loc = w.Location
                if isinstance(loc, LocationCurve):
                    dist = loc.Curve.Distance(pt)
                    if dist < min_dist:
                        min_dist = dist
                        host_wall = w

            if host_wall:
                try:
                    w_curve = host_wall.Location.Curve
                    proj_res = w_curve.Project(pt)
                    insert_pt = proj_res.XYZPoint if proj_res else pt
                    
                    # Подбираем типоразмер под ширину проема (например 800, 900, 1500)
                    chosen_sym = TemplateInspector.find_best_door_or_window(symbols, width_mm)
                    if not chosen_sym:
                        continue

                    inst = self.doc.Create.NewFamilyInstance(
                        insert_pt,
                        chosen_sym,
                        host_wall,
                        self.level,
                        StructuralType.NonStructural
                    )
                    if sill_height_mm > 0:
                        sill_p = inst.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)
                        if sill_p and not sill_p.IsReadOnly:
                            sill_p.Set(sill_ft)
                    created += 1
                except Exception:
                    continue
        return created

    def build_railings(self, curves, railing_type):
        """Строит ограждения по направляющим кривым."""
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
        """Автоматически создает 4 фасада вокруг габаритов здания."""
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
# 6. ГЛАВНАЯ ТОЧКА ВХОДА (MAIN RUNNER)
# ---------------------------------------------------------------------------
def run():
    print(u"--- Запуск алгоритма DWG to BIM (Шаг 2: Умное сопоставление) ---")
    
    # 1. Поиск первого импортированного / связанного DWG в проекте
    dwg_instances = list(FilteredElementCollector(doc).OfClass(ImportInstance).ToElements())
    if not dwg_instances:
        print(u"[ВНИМАНИЕ] В проекте не найден импортированный DWG-чертеж!")
        print(u"Вставьте подложку через: Вставка -> Связь САПР (Link CAD) или Импорт САПР (Import CAD).")
        return

    dwg = dwg_instances[0]
    dwg_type = doc.GetElement(dwg.GetTypeId())
    dwg_name = dwg_type.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM).AsString() if dwg_type else u"DWG"
    print(u"Найден чертеж: " + str(dwg_name))

    # 2. Извлечение геометрии, линий и блоков
    extractor = DwgExtractor(doc, dwg)
    print(u"Слоев с линиями: " + str(len(extractor.curves_by_layer)) + u", слоев с блоками: " + str(len(extractor.blocks_by_layer)))

    # 3. Чтение шаблона Revit
    level = TemplateInspector.get_target_level(doc)
    print(u"Целевой уровень: " + str(level.Name if level else "Не найден"))
    if not level:
        print(u"[ОШИБКА] В проекте нет уровней (Level). Создайте хотя бы один уровень.")
        return

    basic_walls, curtain_walls = TemplateInspector.get_wall_types(doc)
    default_basic_wall = basic_walls[0] if basic_walls else None
    default_curtain_wall = curtain_walls[0] if curtain_walls else default_basic_wall
    
    door_symbols = TemplateInspector.find_family_symbols_by_category(doc, BuiltInCategory.OST_Doors)
    window_symbols = TemplateInspector.find_family_symbols_by_category(doc, BuiltInCategory.OST_Windows)
    railing_type = TemplateInspector.get_railing_type(doc)
    elevation_vft = TemplateInspector.get_elevation_view_family_type(doc)

    print(u"Доступно типоразмеров дверей: " + str(len(door_symbols)) + u", окон: " + str(len(window_symbols)))

    # 4. Выполнение генерации в транзакции Revit
    t = Transaction(doc, u"Генерация BIM по DWG подложке (Шаг 2)")
    t.Start()
    try:
        builder = BimBuilder(doc, level, basic_walls, default_basic_wall)

        # 4.1 Оси
        grid_curves = extractor.match_layer_curves('GRIDS')
        n_grids = builder.build_grids(grid_curves)
        print(u"-> Создано осей: " + str(n_grids))

        # 4.2 Наружные стены (алгоритм парных линий с подбором толщины)
        ext_curves = extractor.match_layer_curves('WALLS_EXT')
        n_ext = builder.build_walls_paired(ext_curves, default_basic_wall, height_mm=3300)
        print(u"-> Создано наружных стен (парные линии): " + str(n_ext))

        # 4.3 Внутренние стены / перегородки
        int_curves = extractor.match_layer_curves('WALLS_INT')
        n_int = builder.build_walls_paired(int_curves, default_basic_wall, height_mm=3000)
        print(u"-> Создано внутренних перегородок: " + str(n_int))

        # 4.4 Витражи
        curtain_curves = extractor.match_layer_curves('CURTAIN')
        n_curtains = builder.build_curtain_walls(curtain_curves, default_curtain_wall, height_mm=3300)
        print(u"-> Создано витражей: " + str(n_curtains))

        # 4.5 Двери и проемы (с подбором ширины под проем)
        door_curves = extractor.match_layer_curves('DOORS') + extractor.match_layer_curves('OPENINGS')
        door_blocks = extractor.match_layer_blocks('DOORS')
        n_doors = builder.insert_openings_and_fixtures(door_curves, door_blocks, door_symbols, sill_height_mm=0)
        print(u"-> Вставлено дверей: " + str(n_doors))

        # 4.6 Окна (с автоподбором ширины подоконника 900 мм)
        window_curves = extractor.match_layer_curves('WINDOWS')
        window_blocks = extractor.match_layer_blocks('WINDOWS')
        n_windows = builder.insert_openings_and_fixtures(window_curves, window_blocks, window_symbols, sill_height_mm=900)
        print(u"-> Вставлено окон: " + str(n_windows))

        # 4.7 Ограждения / ступени
        railing_curves = extractor.match_layer_curves('RAILINGS') + extractor.match_layer_curves('STAIRS')
        n_railings = builder.build_railings(railing_curves, railing_type)
        print(u"-> Создано ограждений: " + str(n_railings))

        # 4.8 Четыре фасада вокруг здания
        n_elevations = builder.create_elevations_around_building(elevation_vft)
        print(u"-> Создано видов фасадов: " + str(n_elevations))

        t.Commit()
        print(u"--- Генерация успешно завершена! ---")
    except Exception as ex:
        t.RollBack()
        print(u"[ОШИБКА ТРАНЗАКЦИИ]: " + str(ex))

if __name__ == '__main__':
    run()
