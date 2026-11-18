# -*- coding: utf-8 -*-
"""
DwgToBimGenerator.py
====================
Универсальный скрипт для IronPython / pyRevit / RevitPythonShell / RevitViewPlacer:
Автоматическое считывание связанного или импортированного DWG-чертежа и построение
на его основе элементов Revit:
 - Оси (Grids)
 - Стены и витражи (Walls & Curtain Walls)
 - Двери и окна (Doors & Windows) с привязкой к стенам
 - Проемы (Wall Openings)
 - Ступени и марши (Stairs / Steps)
 - Ограждения (Railings)
 - Автоматическое создание 4 фасадов (Elevations) вокруг построенного здания
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
# Впишите сюда названия слоев из вашего AutoCAD / DWG файла (регистр не важен):
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

# Коэффициент перевода: если DWG в миллиметрах -> переводим во внутренние футы Revit
MM_TO_FEET = 1.0 / 304.8
FEET_TO_MM = 304.8

def mm(val):
    return val * MM_TO_FEET

# ---------------------------------------------------------------------------
# 2. ИНСПЕКТОР ШАБЛОНА REVIT (TEMPLATE INSPECTOR)
# ---------------------------------------------------------------------------
class TemplateInspector:
    """Анализирует активный шаблон Revit и подбирает нужные типы."""
    
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
        """Находит базовую стену, наиболее близкую по толщине к замеренной в DWG."""
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
    def find_family_symbol(doc, category, keyword=None):
        collector = FilteredElementCollector(doc).OfCategory(category).OfClass(FamilySymbol)
        fallback = None
        for sym in collector:
            if fallback is None:
                fallback = sym
            name_combined = (sym.FamilyName + " " + sym.Name).lower()
            if keyword and keyword.lower() in name_combined:
                if not sym.IsActive:
                    sym.Activate()
                    doc.Regenerate()
                return sym
        if fallback and not fallback.IsActive:
            fallback.Activate()
            doc.Regenerate()
        return fallback

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
# 3. ЧТЕНИЕ ГЕОМЕТРИИ И СЛОЕВ ИЗ DWG (DWG EXTRACTOR)
# ---------------------------------------------------------------------------
class DwgExtractor:
    """Извлекает из ImportInstance геометрию (отрезки, полилинии, блоки) по слоям."""
    
    def __init__(self, doc, dwg_instance):
        self.doc = doc
        self.dwg = dwg_instance
        self.curves_by_layer = {}
        self.blocks = []  # [(name, origin_xyz, rotation_rad, layer)]
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
                inst_geo = obj.GetInstanceGeometry()
                if not inst_geo:
                    continue
                for g in inst_geo:
                    layer_name = self._get_layer_name(g)
                    if isinstance(g, Curve):
                        self.curves_by_layer.setdefault(layer_name, []).append(g)
                    elif isinstance(g, PolyLine):
                        # Разбиваем полилинию на сегменты Line
                        pts = list(g.GetCoordinates())
                        for i in range(len(pts) - 1):
                            p1, p2 = pts[i], pts[i+1]
                            if p1.DistanceTo(p2) > 0.01:
                                try:
                                    self.curves_by_layer.setdefault(layer_name, []).append(Line.CreateBound(p1, p2))
                                except Exception:
                                    pass

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

    def match_layer(self, category_key):
        """Возвращает все кривые, слои которых содержат ключевые слова из словаря."""
        keywords = LAYER_KEYWORDS.get(category_key, [])
        result = []
        for l_name, curves in self.curves_by_layer.items():
            for kw in keywords:
                if kw in l_name:
                    result.extend(curves)
                    break
        return result

# ---------------------------------------------------------------------------
# 4. ГЕНЕРАТОР ЭЛЕМЕНТОВ BIM (BIM BUILDER)
# ---------------------------------------------------------------------------
class BimBuilder:
    def __init__(self, doc, level):
        self.doc = doc
        self.level = level
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
            if c.Length < mm(1500):  # Отсекаем засечки и мелкие штрихи
                continue

            p1, p2 = c.GetEndPoint(0), c.GetEndPoint(1)
            line = Line.CreateBound(XYZ(p1.X, p1.Y, 0), XYZ(p2.X, p2.Y, 0))
            
            try:
                g = Grid.Create(self.doc, line)
                # Определяем направление: горизонтальная или вертикальная
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

    def build_walls(self, curves, wall_type, height_mm=3000):
        """Строит стены по центральным осевым линиям."""
        created = 0
        height_ft = height_mm * MM_TO_FEET
        for c in curves:
            if c.Length < mm(200):
                continue
            try:
                p1 = c.GetEndPoint(0)
                p2 = c.GetEndPoint(1)
                flat_line = Line.CreateBound(XYZ(p1.X, p1.Y, 0), XYZ(p2.X, p2.Y, 0))
                w = Wall.Create(self.doc, flat_line, wall_type.Id, self.level.Id, height_ft, 0, False, False)
                self.created_walls.append(w)
                created += 1
            except Exception:
                continue
        return created

    def insert_openings_and_fixtures(self, curves, category, symbol, sill_height_mm=0):
        """Вставляет двери/окна в ближайшую стену рядом с линией блока/проема из DWG."""
        if not symbol or not self.created_walls:
            return 0
        
        created = 0
        sill_ft = sill_height_mm * MM_TO_FEET
        
        for c in curves:
            mid = c.Evaluate(0.5, True)
            mid_pt = XYZ(mid.X, mid.Y, 0)
            
            # Ищем стену-хозяина на расстоянии не более 400 мм
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
                    # Проецируем точку на осевую линию стены для корректной посадки
                    w_curve = host_wall.Location.Curve
                    proj_res = w_curve.Project(mid_pt)
                    insert_pt = proj_res.XYZPoint if proj_res else mid_pt
                    
                    inst = self.doc.Create.NewFamilyInstance(
                        insert_pt,
                        symbol,
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
        """Строит ограждения по направляющим кривым из DWG."""
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
        """Автоматически создает 4 маркера фасадов вокруг границ построенных стен."""
        if not view_family_type or not self.created_walls:
            return 0

        # Вычисляем общий BoundingBox по стенам
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
        offset = mm(4500) # Отступ маркера от здания 4.5м

        facades_data = [
            (XYZ(center_x, min_y - offset, 0), 0, "Южный фасад (1-N)"),
            (XYZ(max_x + offset, center_y, 0), 1, "Восточный фасад (А-Я)"),
            (XYZ(center_x, max_y + offset, 0), 2, "Северный фасад (N-1)"),
            (XYZ(min_x - offset, center_y, 0), 3, "Западный фасад (Я-А)")
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
# 5. ГЛАВНАЯ ТОЧКА ВХОДА (MAIN RUNNER)
# ---------------------------------------------------------------------------
def run():
    print(u"--- Запуск алгоритма DWG to BIM ---")
    
    # 1. Поиск первого связанного или импортированного DWG в проекте
    dwg_instances = list(FilteredElementCollector(doc).OfClass(ImportInstance).ToElements())
    if not dwg_instances:
        print(u"[ВНИМАНИЕ] В проекте не найден импортированный DWG-чертеж!")
        print(u"Вставьте подложку через: Вставка -> Связь САПР (Link CAD) или Импорт САПР (Import CAD).")
        return

    dwg = dwg_instances[0]
    dwg_type = doc.GetElement(dwg.GetTypeId())
    dwg_name = dwg_type.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM).AsString() if dwg_type else u"DWG"
    print(u"Найден чертеж: " + str(dwg_name))

    # 2. Извлечение слоев и геометрии
    extractor = DwgExtractor(doc, dwg)
    print(u"Обнаружено слоев в DWG: " + str(len(extractor.curves_by_layer)))

    # 3. Чтение шаблона Revit
    level = TemplateInspector.get_target_level(doc)
    print(u"Целевой уровень: " + str(level.Name if level else "Не найден"))
    if not level:
        print(u"[ОШИБКА] В проекте нет уровней (Level). Создайте хотя бы один уровень.")
        return

    basic_walls, curtain_walls = TemplateInspector.get_wall_types(doc)
    default_basic_wall = basic_walls[0] if basic_walls else None
    default_curtain_wall = curtain_walls[0] if curtain_walls else default_basic_wall
    door_symbol = TemplateInspector.find_family_symbol(doc, BuiltInCategory.OST_Doors)
    window_symbol = TemplateInspector.find_family_symbol(doc, BuiltInCategory.OST_Windows)
    railing_type = TemplateInspector.get_railing_type(doc)
    elevation_vft = TemplateInspector.get_elevation_view_family_type(doc)

    print(u"Базовая стена: " + str(default_basic_wall.Name if default_basic_wall else "Нет"))
    print(u"Витраж: " + str(default_curtain_wall.Name if default_curtain_wall else "Нет"))
    print(u"Дверь: " + str(door_symbol.FamilyName if door_symbol else "Нет"))
    print(u"Окно: " + str(window_symbol.FamilyName if window_symbol else "Нет"))

    # 4. Выполнение генерации в транзакции Revit
    t = Transaction(doc, u"Генерация BIM по DWG подложке")
    t.Start()
    try:
        builder = BimBuilder(doc, level)

        # 4.1 Оси
        grid_curves = extractor.match_layer('GRIDS')
        n_grids = builder.build_grids(grid_curves)
        print(u"-> Создано осей: " + str(n_grids))

        # 4.2 Наружные стены
        ext_wall_curves = extractor.match_layer('WALLS_EXT')
        n_ext_walls = builder.build_walls(ext_wall_curves, default_basic_wall, height_mm=3300)
        print(u"-> Создано наружных стен: " + str(n_ext_walls))

        # 4.3 Внутренние стены / перегородки
        int_wall_curves = extractor.match_layer('WALLS_INT')
        n_int_walls = builder.build_walls(int_wall_curves, default_basic_wall, height_mm=3000)
        print(u"-> Создано внутренних перегородок: " + str(n_int_walls))

        # 4.4 Витражи
        curtain_curves = extractor.match_layer('CURTAIN')
        n_curtains = builder.build_walls(curtain_curves, default_curtain_wall, height_mm=3300)
        print(u"-> Создано витражей: " + str(n_curtains))

        # 4.5 Двери и проемы
        door_curves = extractor.match_layer('DOORS')
        n_doors = builder.insert_openings_and_fixtures(door_curves, BuiltInCategory.OST_Doors, door_symbol, sill_height_mm=0)
        print(u"-> Вставлено дверей: " + str(n_doors))

        # 4.6 Окна
        window_curves = extractor.match_layer('WINDOWS')
        n_windows = builder.insert_openings_and_fixtures(window_curves, BuiltInCategory.OST_Windows, window_symbol, sill_height_mm=900)
        print(u"-> Вставлено окон: " + str(n_windows))

        # 4.7 Ограждения / ступени
        railing_curves = extractor.match_layer('RAILINGS') + extractor.match_layer('STAIRS')
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

# Запуск функции
if __name__ == '__main__':
    run()
