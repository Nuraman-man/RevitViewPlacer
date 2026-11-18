using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitViewPlacer
{
    /// <summary>
    /// Анализ дверей и помещений модели, планировщик размещения зон генплана без наложений
    /// и отложенная отрисовка марок (кружков с номерами) по ГОСТ 21.508 после выбора масштаба.
    /// </summary>
    public partial class GenerateDetailedSitePlanCommand
    {
        private const double OneMeter = 1000.0 * MmToFeet;

        // =========================================================================
        // 1. ОТЛОЖЕННЫЕ МАРКИ ГЕНПЛАНА (рисуются после выбора итогового масштаба вида)
        // =========================================================================

        [ThreadStatic]
        private static List<(XYZ Center, int Number)>? s_pendingPlanMarkers;

        private static void BeginPlanMarkers()
        {
            s_pendingPlanMarkers = [];
        }

        private static void RegisterPlanMarker(XYZ center, int number)
        {
            s_pendingPlanMarkers ??= [];
            s_pendingPlanMarkers.RemoveAll(m => m.Number == number);
            s_pendingPlanMarkers.Add((center, number));
        }

        /// <summary>
        /// Удаляет 2D-аннотации (тексты, кружки, заливки) предыдущих запусков с переиспользуемого вида генплана.
        /// </summary>
        private static void CleanupSitePlanAnnotations(Document doc, View view)
        {
            try
            {
                List<ElementId> ids = new FilteredElementCollector(doc)
                    .OwnedByView(view.Id)
                    .WhereElementIsNotElementType()
                    .Where(e => e is TextNote || e is FilledRegion || e is DetailCurve)
                    .Select(e => e.Id)
                    .ToList();

                if (ids.Count > 0)
                {
                    doc.Delete(ids);
                    doc.Regenerate();
                }
            }
            catch
            {
                // Продолжаем построение
            }
        }

        /// <summary>
        /// Рисует марки на плане: белый круг Ø8 мм (на листе) с контуром и номер, точно отцентрованный по кругу.
        /// Вызывается после того, как вид получил итоговый масштаб на листе.
        /// </summary>
        private static void DrawPendingPlanMarkers(Document doc, ViewPlan view, int scale)
        {
            if (s_pendingPlanMarkers == null || s_pendingPlanMarkers.Count == 0)
            {
                return;
            }

            if (scale <= 0)
            {
                scale = view.Scale > 0 ? view.Scale : 500;
            }

            double radius = 4.0 * MmToFeet * scale;
            double z = view.GenLevel?.Elevation ?? 0.0;
            ElementId regionTypeId = GetOrCreateWhiteMarkerRegionType(doc);
            ElementId textTypeId = GetOrCreateGostTextNoteTypeId(doc, 3.5, bold: true, suffix: "НомерМарки_3.5мм");
            EnsureViewSketchPlane(doc, view);

            List<(TextNote Note, XYZ Center)> notes = [];

            foreach ((XYZ c, int number) in s_pendingPlanMarkers.OrderBy(m => m.Number))
            {
                XYZ center = new XYZ(c.X, c.Y, z);
                bool regionCreated = false;

                if (regionTypeId != ElementId.InvalidElementId)
                {
                    try
                    {
                        CurveLoop circle = new CurveLoop();
                        circle.Append(Arc.Create(center, radius, 0.0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                        circle.Append(Arc.Create(center, radius, Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                        FilledRegion.Create(doc, regionTypeId, view.Id, [circle]);
                        regionCreated = true;
                    }
                    catch
                    {
                        regionCreated = false;
                    }
                }

                if (!regionCreated)
                {
                    try
                    {
                        doc.Create.NewDetailCurve(view, Arc.Create(center, radius, 0.0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                        doc.Create.NewDetailCurve(view, Arc.Create(center, radius, Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                    }
                    catch
                    {
                    }
                }

                if (textTypeId == ElementId.InvalidElementId)
                {
                    continue;
                }

                try
                {
                    TextNoteOptions opt = new TextNoteOptions(textTypeId)
                    {
                        HorizontalAlignment = HorizontalTextAlignment.Center,
                        VerticalAlignment = VerticalTextAlignment.Middle,
                        Rotation = 0.0
                    };
                    TextNote note = TextNote.Create(doc, view.Id, center, number.ToString(), opt);
                    notes.Add((note, center));
                }
                catch
                {
                }
            }

            // Точное центрирование: сдвигаем каждый текст так, чтобы центр его габарита совпал с центром круга
            try
            {
                doc.Regenerate();
                foreach ((TextNote note, XYZ center) in notes)
                {
                    BoundingBoxXYZ? bb = note.get_BoundingBox(view);
                    if (bb == null)
                    {
                        continue;
                    }

                    XYZ delta = new XYZ(
                        center.X - ((bb.Min.X + bb.Max.X) / 2.0),
                        center.Y - ((bb.Min.Y + bb.Max.Y) / 2.0),
                        0.0);

                    if (delta.GetLength() > 1e-6)
                    {
                        ElementTransformUtils.MoveElement(doc, note.Id, delta);
                    }
                }
            }
            catch
            {
            }

            s_pendingPlanMarkers.Clear();
        }

        private static ElementId GetOrCreateWhiteMarkerRegionType(Document doc)
        {
            const string typeName = "ГП_Марка_БелаяЗаливка";

            List<FilledRegionType> types = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .ToList();

            FilledRegionType? existing = types.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return existing.Id;
            }

            FilledRegionType? baseType = types.FirstOrDefault();
            if (baseType == null)
            {
                return ElementId.InvalidElementId;
            }

            ElementId solidId = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f =>
                {
                    FillPattern fp = f.GetFillPattern();
                    return fp.IsSolidFill && fp.Target == FillPatternTarget.Drafting;
                })?.Id ?? ElementId.InvalidElementId;

            try
            {
                FilledRegionType newType = (FilledRegionType)baseType.Duplicate(typeName);
                if (solidId != ElementId.InvalidElementId)
                {
                    try { newType.ForegroundPatternId = solidId; } catch { }
                }
                try { newType.ForegroundPatternColor = new Color(255, 255, 255); } catch { }
                try { newType.BackgroundPatternId = ElementId.InvalidElementId; } catch { }
                return newType.Id;
            }
            catch
            {
                return baseType.Id;
            }
        }

        // =========================================================================
        // 2. АНАЛИЗ ДВЕРЕЙ И ПОМЕЩЕНИЙ МОДЕЛИ
        // =========================================================================

        private enum RoomKind
        {
            Other,
            Entrance,
            Hall,
            Kitchen,
            Service,
            Technical,
            Staff
        }

        private sealed record SiteRoomInfo(string Name, XYZ Center, double AreaM2, RoomKind Kind);

        private sealed record SiteDoorInfo(
            XYZ Point,
            XYZ Outward,
            double WidthM,
            string RoomName,
            RoomKind Kind,
            double MainScore,
            double ServiceScore,
            BuildingSide Side,
            XYZ FacadePoint,
            bool IsSynthetic);

        private sealed record BuildingDoorRoomAnalysis(
            List<SiteDoorInfo> ExteriorDoors,
            SiteDoorInfo? MainDoor,
            SiteDoorInfo? ServiceDoor,
            List<SiteRoomInfo> Rooms,
            string Summary);

        private static bool ContainsAny(string text, params string[] keys)
        {
            foreach (string k in keys)
            {
                if (text.Contains(k, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static RoomKind ClassifyRoomName(string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return RoomKind.Other;
            }

            string n = rawName.ToLowerInvariant();

            if (ContainsAny(n, "тамбур", "вестибюл", "холл", "фойе", "вход", "лобби", "ресепш", "гардероб", "lobby", "entrance"))
                return RoomKind.Entrance;
            if (ContainsAny(n, "кухн", "горяч", "холодный цех", "цех", "заготов", "мойк", "посудомо", "пищеблок", "раздаточ", "kitchen"))
                return RoomKind.Kitchen;
            if (ContainsAny(n, "загруз", "разгруз", "склад", "кладов", "хоз", "экспедиц", "тара", "мусор", "отход", "приемк", "приёмк", "storage"))
                return RoomKind.Service;
            if (ContainsAny(n, "электр", "щитов", "венткам", "вентиляц", "итп", "тепловой", "котел", "котёл", "котель", "насос", "серверн", "техн", "водомер"))
                return RoomKind.Technical;
            if (ContainsAny(n, "персонал", "сотрудник", "раздевал", "бытов", "служеб", "админ", "кабинет", "директор", "staff"))
                return RoomKind.Staff;
            if (ContainsAny(n, "зал", "обеден", "банкет", "бар", "веранд", "кафе", "ресторан", "dining", "hall"))
                return RoomKind.Hall;

            return RoomKind.Other;
        }

        private static string GetRoomDisplayName(Room room)
        {
            string? name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
            return string.IsNullOrWhiteSpace(name) ? room.Name : name.Trim();
        }

        private static Room? GetRoomAtPointSafe(Document doc, XYZ pt)
        {
            try
            {
                return doc.GetRoomAtPoint(pt);
            }
            catch
            {
                return null;
            }
        }

        private static double GetDoorWidthFt(FamilyInstance door)
        {
            double w = door.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0.0;
            if (w <= 1e-6) w = door.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0.0;
            if (w <= 1e-6) w = door.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM)?.AsDouble() ?? 0.0;
            if (w <= 1e-6) w = door.Symbol?.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM)?.AsDouble() ?? 0.0;
            return w;
        }

        /// <summary>
        /// Луч из точки двери в направлении «наружу» до пересечения с габаритом здания:
        /// определяет фасад (сторону) и точку на габарите, к которой привязываются зоны генплана.
        /// </summary>
        private static (BuildingSide Side, XYZ Point) CastToBuildingBox(Rect2 box, XYZ pt, XYZ dir, double z)
        {
            double tBest = double.MaxValue;
            BuildingSide side = BuildingSide.South;

            void Try(double t, BuildingSide s)
            {
                if (t >= -1e-6 && t < tBest)
                {
                    tBest = t;
                    side = s;
                }
            }

            if (dir.X > 1e-6) Try((box.MaxX - pt.X) / dir.X, BuildingSide.East);
            if (dir.X < -1e-6) Try((box.MinX - pt.X) / dir.X, BuildingSide.West);
            if (dir.Y > 1e-6) Try((box.MaxY - pt.Y) / dir.Y, BuildingSide.North);
            if (dir.Y < -1e-6) Try((box.MinY - pt.Y) / dir.Y, BuildingSide.South);

            if (tBest == double.MaxValue)
            {
                return NearestFacade(box, pt, z);
            }

            tBest = Math.Max(0.0, tBest);
            XYZ hit = new XYZ(
                Math.Clamp(pt.X + (dir.X * tBest), box.MinX, box.MaxX),
                Math.Clamp(pt.Y + (dir.Y * tBest), box.MinY, box.MaxY),
                z);
            return (side, hit);
        }

        private static (BuildingSide Side, XYZ Point) NearestFacade(Rect2 box, XYZ p, double z)
        {
            double dS = Math.Abs(p.Y - box.MinY);
            double dN = Math.Abs(box.MaxY - p.Y);
            double dW = Math.Abs(p.X - box.MinX);
            double dE = Math.Abs(box.MaxX - p.X);
            double min = Math.Min(Math.Min(dS, dN), Math.Min(dW, dE));

            double cx = Math.Clamp(p.X, box.MinX, box.MaxX);
            double cy = Math.Clamp(p.Y, box.MinY, box.MaxY);

            if (min == dS) return (BuildingSide.South, new XYZ(cx, box.MinY, z));
            if (min == dN) return (BuildingSide.North, new XYZ(cx, box.MaxY, z));
            if (min == dW) return (BuildingSide.West, new XYZ(box.MinX, cy, z));
            return (BuildingSide.East, new XYZ(box.MaxX, cy, z));
        }

        private static XYZ SideNormal(BuildingSide side) => side switch
        {
            BuildingSide.South => -XYZ.BasisY,
            BuildingSide.North => XYZ.BasisY,
            BuildingSide.West => -XYZ.BasisX,
            _ => XYZ.BasisX
        };

        private static BuildingSide OppositeSide(BuildingSide side) => side switch
        {
            BuildingSide.South => BuildingSide.North,
            BuildingSide.North => BuildingSide.South,
            BuildingSide.West => BuildingSide.East,
            _ => BuildingSide.West
        };

        private static string SideRu(BuildingSide side) => side switch
        {
            BuildingSide.South => "юг",
            BuildingSide.North => "север",
            BuildingSide.West => "запад",
            _ => "восток"
        };

        private static SiteDoorInfo CreateSyntheticDoor(Rect2 box, BuildingSide side, double z, string label)
        {
            XYZ p = side switch
            {
                BuildingSide.South => new XYZ(box.CenterX, box.MinY, z),
                BuildingSide.North => new XYZ(box.CenterX, box.MaxY, z),
                BuildingSide.West => new XYZ(box.MinX, box.CenterY, z),
                _ => new XYZ(box.MaxX, box.CenterY, z)
            };
            return new SiteDoorInfo(p, SideNormal(side), 0.0, label, RoomKind.Other, 0.0, 0.0, side, p, IsSynthetic: true);
        }

        /// <summary>
        /// Находит наружные двери первого этажа, определяет помещение за каждой дверью (по Room),
        /// направление «наружу» и выбирает главный и служебный входы по назначению помещений.
        /// </summary>
        private static BuildingDoorRoomAnalysis AnalyzeBuildingDoorsAndRooms(
            Document doc,
            Level level,
            BoundingBoxXYZ buildingBox,
            List<Wall> exteriorWalls)
        {
            double baseZ = level.Elevation;
            Rect2 box = new Rect2(buildingBox.Min.X, buildingBox.Min.Y, buildingBox.Max.X, buildingBox.Max.Y);
            XYZ center = new XYZ(box.CenterX, box.CenterY, baseZ);
            HashSet<ElementId> extWallIds = exteriorWalls.Select(w => w.Id).ToHashSet();

            // --- Помещения первого этажа ---
            List<SiteRoomInfo> rooms = [];
            try
            {
                foreach (Room room in new FilteredElementCollector(doc)
                             .OfCategory(BuiltInCategory.OST_Rooms)
                             .WhereElementIsNotElementType()
                             .OfType<Room>())
                {
                    if (room.Area <= 1e-6 || room.Location is not LocationPoint lp)
                        continue;

                    bool onLevel = room.LevelId == level.Id ||
                                   (room.Level != null && Math.Abs(room.Level.Elevation - baseZ) < 2.0);
                    if (!onLevel)
                        continue;

                    string name = GetRoomDisplayName(room);
                    double areaM2 = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters);
                    rooms.Add(new SiteRoomInfo(name, lp.Point, areaM2, ClassifyRoomName(name)));
                }
            }
            catch
            {
            }

            // --- Двери первого этажа ---
            List<FamilyInstance> doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(d =>
                {
                    if (d.LevelId == level.Id) return true;
                    BoundingBoxXYZ? b = d.get_BoundingBox(null);
                    return b != null && Math.Abs(b.Min.Z - baseZ) < 3000.0 * MmToFeet;
                })
                .ToList();

            List<SiteDoorInfo> exteriorDoors = [];
            double probeDist = 900.0 * MmToFeet;
            double probeZ = baseZ + (1000.0 * MmToFeet);

            foreach (FamilyInstance door in doors)
            {
                if (door.Location is not LocationPoint dlp)
                    continue;

                XYZ pt = new XYZ(dlp.Point.X, dlp.Point.Y, baseZ);
                XYZ facing = new XYZ(door.FacingOrientation.X, door.FacingOrientation.Y, 0.0);
                if (facing.GetLength() < 1e-6 && door.Host is Wall hostW)
                {
                    facing = new XYZ(hostW.Orientation.X, hostW.Orientation.Y, 0.0);
                }
                if (facing.GetLength() < 1e-6)
                    continue;
                facing = facing.Normalize();

                bool hostExterior = door.Host is Wall hw &&
                                    (extWallIds.Contains(hw.Id) || hw.WallType?.Function == WallFunction.Exterior);

                Room? roomA = rooms.Count > 0 ? GetRoomAtPointSafe(doc, new XYZ(pt.X + (facing.X * probeDist), pt.Y + (facing.Y * probeDist), probeZ)) : null;
                Room? roomB = rooms.Count > 0 ? GetRoomAtPointSafe(doc, new XYZ(pt.X - (facing.X * probeDist), pt.Y - (facing.Y * probeDist), probeZ)) : null;

                bool exactlyOneOutside = (roomA == null) ^ (roomB == null);
                bool isExterior = rooms.Count > 0
                    ? exactlyOneOutside && (hostExterior || extWallIds.Count == 0)
                    : hostExterior;

                if (!isExterior)
                    continue;

                XYZ outward;
                if (roomA == null && roomB != null) outward = facing;
                else if (roomB == null && roomA != null) outward = facing.Negate();
                else
                {
                    XYZ toDoor = new XYZ(pt.X - center.X, pt.Y - center.Y, 0.0);
                    outward = facing.DotProduct(toDoor) >= 0.0 ? facing : facing.Negate();
                }

                Room? interior = roomA ?? roomB;
                if (interior == null)
                {
                    try { interior = door.FromRoom ?? door.ToRoom; } catch { interior = null; }
                }

                string roomName = interior != null ? GetRoomDisplayName(interior) : "помещение не задано";
                RoomKind kind = interior != null ? ClassifyRoomName(roomName) : RoomKind.Other;
                double widthM = UnitUtils.ConvertFromInternalUnits(GetDoorWidthFt(door), UnitTypeId.Meters);
                string typeText = ((door.Symbol?.FamilyName ?? "") + " " + door.Name).ToLowerInvariant();

                double mainScore = widthM * 4.0;
                double serviceScore = 0.0;
                switch (kind)
                {
                    case RoomKind.Entrance: mainScore += 12.0; serviceScore -= 6.0; break;
                    case RoomKind.Hall: mainScore += 7.0; serviceScore -= 6.0; break;
                    case RoomKind.Kitchen: serviceScore += 10.0; mainScore -= 8.0; break;
                    case RoomKind.Service: serviceScore += 12.0; mainScore -= 8.0; break;
                    case RoomKind.Technical: serviceScore += 6.0; mainScore -= 8.0; break;
                    case RoomKind.Staff: serviceScore += 7.0; mainScore -= 8.0; break;
                    default: mainScore += 1.0; break;
                }

                if (ContainsAny(typeText, "двупол", "двуст", "double", "витраж", "стекл", "автомат", "входн", "вход"))
                    mainScore += 4.0;
                if (ContainsAny(typeText, "техн", "противопож", "металл", "служеб", "ei"))
                    serviceScore += 2.0;

                (BuildingSide side, XYZ facadePt) = CastToBuildingBox(box, pt, outward, baseZ);

                exteriorDoors.Add(new SiteDoorInfo(pt, outward, widthM, roomName, kind, mainScore, serviceScore, side, facadePt, IsSynthetic: false));
            }

            SiteDoorInfo? mainDoor = exteriorDoors
                .OrderByDescending(d => d.MainScore)
                .ThenByDescending(d => d.WidthM)
                .FirstOrDefault();

            SiteDoorInfo? serviceDoor = null;
            if (mainDoor != null)
            {
                List<SiteDoorInfo> others = exteriorDoors.Where(d => !ReferenceEquals(d, mainDoor)).ToList();
                serviceDoor = others
                    .Where(d => d.ServiceScore > 0.0)
                    .OrderByDescending(d => d.ServiceScore + (d.Point.DistanceTo(mainDoor.Point) * 0.3048 * 0.15))
                    .FirstOrDefault()
                    ?? others
                        .Where(d => d.Point.DistanceTo(mainDoor.Point) > 4.0 * OneMeter)
                        .OrderByDescending(d => d.Point.DistanceTo(mainDoor.Point))
                        .FirstOrDefault();
            }

            string summary;
            if (mainDoor == null)
            {
                summary = rooms.Count > 0
                    ? $"помещений: {rooms.Count}, наружные двери не найдены — вход принят с юга"
                    : "помещения и наружные двери не найдены — вход принят с юга";
            }
            else
            {
                summary = $"помещений: {rooms.Count}, наружных дверей: {exteriorDoors.Count}; " +
                          $"гл. вход — «{mainDoor.RoomName}» ({SideRu(mainDoor.Side)})";
                summary += serviceDoor != null
                    ? $", служ. вход — «{serviceDoor.RoomName}» ({SideRu(serviceDoor.Side)})"
                    : ", служ. вход не найден — хоззона с противоположной стороны";
            }

            return new BuildingDoorRoomAnalysis(exteriorDoors, mainDoor, serviceDoor, rooms, summary);
        }

        // =========================================================================
        // 3. ПЛАНИРОВЩИК РАЗМЕЩЕНИЯ ЗОН БЕЗ НАЛОЖЕНИЙ
        // =========================================================================

        private readonly record struct Rect2(double MinX, double MinY, double MaxX, double MaxY)
        {
            public double W => MaxX - MinX;
            public double H => MaxY - MinY;
            public double CenterX => (MinX + MaxX) / 2.0;
            public double CenterY => (MinY + MaxY) / 2.0;
            public double AreaM2 => W * H * 0.3048 * 0.3048;

            public Rect2 Expand(double d) => new Rect2(MinX - d, MinY - d, MaxX + d, MaxY + d);

            public bool Intersects(Rect2 o, double gap) =>
                !(MaxX + gap <= o.MinX || o.MaxX + gap <= MinX || MaxY + gap <= o.MinY || o.MaxY + gap <= MinY);

            public bool Contains(XYZ p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

            public XYZ Center(double z) => new XYZ(CenterX, CenterY, z);

            public CurveLoop ToLoop(double z) => CreateRectangleLoop(MinX, MinY, MaxX, MaxY, z);
        }

        private static bool PointInPolygon(List<XYZ> poly, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                XYZ a = poly[i];
                XYZ b = poly[j];
                if (((a.Y > y) != (b.Y > y)) &&
                    (x < ((b.X - a.X) * (y - a.Y) / ((b.Y - a.Y) + 1e-12)) + a.X))
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private sealed class SiteLayoutPlanner
        {
            private readonly List<XYZ> _poly;
            private readonly double _boundaryMargin;
            private readonly double _gap;

            public Rect2 Ring { get; }
            public Rect2 SiteBox { get; }
            public List<Rect2> Occupied { get; } = [];

            public SiteLayoutPlanner(Rect2 ring, List<XYZ> sitePoly, double boundaryMargin, double gap)
            {
                Ring = ring;
                _poly = sitePoly;
                _boundaryMargin = boundaryMargin;
                _gap = gap;
                SiteBox = new Rect2(sitePoly.Min(p => p.X), sitePoly.Min(p => p.Y), sitePoly.Max(p => p.X), sitePoly.Max(p => p.Y));
            }

            public bool InsideSite(double x, double y) => PointInPolygon(_poly, x, y);

            public bool IsFree(XYZ p, double clearance)
            {
                if (Ring.Expand(clearance).Contains(p)) return false;
                foreach (Rect2 r in Occupied)
                {
                    if (r.Expand(clearance).Contains(p)) return false;
                }
                return true;
            }

            private bool Fits(Rect2 r, double gap)
            {
                Rect2 e = r.Expand(_boundaryMargin);
                if (!InsideSite(e.MinX, e.MinY) || !InsideSite(e.MaxX, e.MinY) ||
                    !InsideSite(e.MaxX, e.MaxY) || !InsideSite(e.MinX, e.MaxY))
                {
                    return false;
                }

                if (r.Intersects(Ring, -1e-4))
                {
                    return false;
                }

                foreach (Rect2 o in Occupied)
                {
                    if (r.Intersects(o, gap)) return false;
                }
                return true;
            }

            private static IEnumerable<BuildingSide> SideOrder(BuildingSide preferred)
            {
                yield return preferred;
                switch (preferred)
                {
                    case BuildingSide.South: yield return BuildingSide.West; yield return BuildingSide.East; break;
                    case BuildingSide.North: yield return BuildingSide.East; yield return BuildingSide.West; break;
                    case BuildingSide.West: yield return BuildingSide.South; yield return BuildingSide.North; break;
                    default: yield return BuildingSide.North; yield return BuildingSide.South; break;
                }
                yield return OppositeSide(preferred);
            }

            private Rect2 Build(BuildingSide side, double c, double along, double depth, double ringGap, double push) => side switch
            {
                BuildingSide.South => new Rect2(c - (along / 2.0), Ring.MinY - ringGap - push - depth, c + (along / 2.0), Ring.MinY - ringGap - push),
                BuildingSide.North => new Rect2(c - (along / 2.0), Ring.MaxY + ringGap + push, c + (along / 2.0), Ring.MaxY + ringGap + push + depth),
                BuildingSide.West => new Rect2(Ring.MinX - ringGap - push - depth, c - (along / 2.0), Ring.MinX - ringGap - push, c + (along / 2.0)),
                _ => new Rect2(Ring.MaxX + ringGap + push, c - (along / 2.0), Ring.MaxX + ringGap + push + depth, c + (along / 2.0))
            };

            private Rect2? PlaceOnSide(XYZ anchor, BuildingSide side, double along, double depth, double ringGap)
            {
                bool horizontal = side is BuildingSide.South or BuildingSide.North;
                double a0 = horizontal
                    ? Math.Clamp(anchor.X, Ring.MinX, Ring.MaxX)
                    : Math.Clamp(anchor.Y, Ring.MinY, Ring.MaxY);

                List<(double Score, Rect2 R)> candidates = [];
                for (int k = 0; k <= 14; k++)
                {
                    for (int j = -45; j <= 45; j++)
                    {
                        double off = j * OneMeter;
                        double push = k * OneMeter;
                        candidates.Add((Math.Abs(off) + (push * 1.6), Build(side, a0 + off, along, depth, ringGap, push)));
                    }
                }

                foreach ((double _, Rect2 r) in candidates.OrderBy(c => c.Score))
                {
                    if (Fits(r, _gap)) return r;
                }
                return null;
            }

            /// <summary>
            /// Размещает прямоугольную зону у фасада side как можно ближе к anchor (по оси фасада),
            /// без пересечения с кольцевым проездом и ранее размещенными зонами; при нехватке места
            /// пробует соседние фасады и уменьшенные размеры.
            /// </summary>
            public Rect2? Place(XYZ anchor, BuildingSide side, double along, double depth, double ringGap, bool allowOtherSides = true)
            {
                foreach (double k in new[] { 1.0, 0.8, 0.65 })
                {
                    IEnumerable<BuildingSide> sides = allowOtherSides ? SideOrder(side) : [side];
                    foreach (BuildingSide s in sides)
                    {
                        Rect2? r = PlaceOnSide(anchor, s, along * k, depth * k, ringGap);
                        if (r != null)
                        {
                            Occupied.Add(r.Value);
                            return r;
                        }
                    }
                }
                return null;
            }

            /// <summary>
            /// Въезд: полоса от кольцевого проезда до красной линии участка у фасада side, ближе к anchor.
            /// </summary>
            public Rect2? PlaceDriveway(BuildingSide side, XYZ anchor, double width)
            {
                bool horizontal = side is BuildingSide.South or BuildingSide.North;
                double a0 = horizontal ? anchor.X : anchor.Y;
                double pull = 600.0 * MmToFeet;

                List<(double Score, Rect2 R)> candidates = [];
                for (int j = -50; j <= 50; j++)
                {
                    double c = a0 + (j * OneMeter);
                    Rect2 r = side switch
                    {
                        BuildingSide.South => new Rect2(c - (width / 2.0), SiteBox.MinY, c + (width / 2.0), Ring.MinY),
                        BuildingSide.North => new Rect2(c - (width / 2.0), Ring.MaxY, c + (width / 2.0), SiteBox.MaxY),
                        BuildingSide.West => new Rect2(SiteBox.MinX, c - (width / 2.0), Ring.MinX, c + (width / 2.0)),
                        _ => new Rect2(Ring.MaxX, c - (width / 2.0), SiteBox.MaxX, c + (width / 2.0))
                    };
                    candidates.Add((Math.Abs(j), r));
                }

                foreach ((double _, Rect2 r) in candidates.OrderBy(c => c.Score))
                {
                    // Крайние точки въезда у красной линии и у проезда должны лежать внутри участка
                    (double x1, double y1, double x2, double y2) = side switch
                    {
                        BuildingSide.South => (r.MinX, r.MinY + pull, r.MaxX, r.MinY + pull),
                        BuildingSide.North => (r.MinX, r.MaxY - pull, r.MaxX, r.MaxY - pull),
                        BuildingSide.West => (r.MinX + pull, r.MinY, r.MinX + pull, r.MaxY),
                        _ => (r.MaxX - pull, r.MinY, r.MaxX - pull, r.MaxY)
                    };
                    if (!InsideSite(x1, y1) || !InsideSite(x2, y2)) continue;

                    bool clash = Occupied.Any(o => r.Intersects(o, 500.0 * MmToFeet));
                    if (clash) continue;

                    Occupied.Add(r);
                    return r;
                }
                return null;
            }
        }

        // =========================================================================
        // 4. ГЕОМЕТРИЧЕСКИЕ ПОМОЩНИКИ ДЛЯ ЗОН ПО АНАЛИЗУ МОДЕЛИ
        // =========================================================================

        private static CurveLoop? CreateStripLoop(XYZ p0, XYZ p1, double width, double z)
        {
            XYZ d = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0.0);
            double len = d.GetLength();
            if (len < 300.0 * MmToFeet)
            {
                return null;
            }

            d = d / len;
            XYZ n = new XYZ(-d.Y, d.X, 0.0) * (width / 2.0);
            XYZ a = new XYZ(p0.X + n.X, p0.Y + n.Y, z);
            XYZ b = new XYZ(p1.X + n.X, p1.Y + n.Y, z);
            XYZ c = new XYZ(p1.X - n.X, p1.Y - n.Y, z);
            XYZ e = new XYZ(p0.X - n.X, p0.Y - n.Y, z);

            CurveLoop loop = new CurveLoop();
            loop.Append(Line.CreateBound(a, b));
            loop.Append(Line.CreateBound(b, c));
            loop.Append(Line.CreateBound(c, e));
            loop.Append(Line.CreateBound(e, a));
            if (!loop.IsCounterclockwise(XYZ.BasisZ))
            {
                loop.Flip();
            }
            return loop;
        }

        /// <summary>
        /// Пешеходный подход от реальной двери до габарита здания (плитка) и «зебра» через кольцевой проезд.
        /// </summary>
        private static double CreateDoorApproach(
            Document doc,
            ElementId siteCatId,
            SiteDoorInfo door,
            Rect2 building,
            Rect2 ring,
            double width,
            double baseZ,
            SiteMaterials materials,
            string label,
            List<CurveLoop> extraWalkways)
        {
            double area = 0.0;
            if (!door.IsSynthetic)
            {
                CurveLoop? strip = CreateStripLoop(door.Point, door.FacadePoint, width, baseZ - (250.0 * MmToFeet));
                if (strip != null)
                {
                    Create3DExtrusionDirectShape(doc, siteCatId, [strip], 150.0 * MmToFeet, materials.PavingTileMaterialId,
                        $"ГП_3D: Дорожка от {label} (дверь помещения «{door.RoomName}»)", "ГП-ТРОТ");
                    area = CalculateCurveLoopAreaM2(strip);
                    extraWalkways.Add(ShiftLoopZ(strip, baseZ));
                }
            }

            CreateRingCrossing(doc, siteCatId, door.Side, door.Side is BuildingSide.South or BuildingSide.North ? door.FacadePoint.X : door.FacadePoint.Y,
                building, ring, width + (600.0 * MmToFeet), baseZ, materials, $"ГП_3D: Пешеходный переход у {label}");
            return area;
        }

        private static void CreateRingCrossing(
            Document doc,
            ElementId siteCatId,
            BuildingSide side,
            double along,
            Rect2 building,
            Rect2 ring,
            double width,
            double baseZ,
            SiteMaterials materials,
            string comment)
        {
            List<GeometryObject> stripes = [];
            double step = 700.0 * MmToFeet;
            double bar = 400.0 * MmToFeet;
            double half = width / 2.0;
            double z = baseZ - (101.0 * MmToFeet);
            double blind = BlindAreaOffsetMm * MmToFeet;

            try
            {
                switch (side)
                {
                    case BuildingSide.South:
                    case BuildingSide.North:
                    {
                        double y0 = side == BuildingSide.South ? ring.MinY : building.MaxY + blind;
                        double y1 = side == BuildingSide.South ? building.MinY - blind : ring.MaxY;
                        for (double y = y0 + (200.0 * MmToFeet); y + bar <= y1 - (200.0 * MmToFeet); y += step)
                        {
                            stripes.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                                [CreateRectangleLoop(along - half, y, along + half, y + bar, z)], XYZ.BasisZ, 4.0 * MmToFeet,
                                new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));
                        }
                        break;
                    }
                    default:
                    {
                        double x0 = side == BuildingSide.West ? ring.MinX : building.MaxX + blind;
                        double x1 = side == BuildingSide.West ? building.MinX - blind : ring.MaxX;
                        for (double x = x0 + (200.0 * MmToFeet); x + bar <= x1 - (200.0 * MmToFeet); x += step)
                        {
                            stripes.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                                [CreateRectangleLoop(x, along - half, x + bar, along + half, z)], XYZ.BasisZ, 4.0 * MmToFeet,
                                new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));
                        }
                        break;
                    }
                }

                if (stripes.Count > 0)
                {
                    DirectShape ds = DirectShape.CreateElement(doc, siteCatId);
                    ds.SetShape(stripes);
                    SetZoneMetadata(ds, comment, "ГП-РАЗМ");
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Автостоянка в прямоугольнике у кольцевого проезда: разметка перпендикулярно проезду,
        /// колесоотбойники у дальнего края.
        /// </summary>
        private static void CreateParkingLotOnRect(
            Document doc,
            ElementId siteCatId,
            Rect2 r,
            BuildingSide side,
            int spots,
            double baseZ,
            SiteMaterials materials,
            string comment)
        {
            Create3DExtrusionDirectShape(doc, siteCatId, [r.ToLoop(baseZ - (450.0 * MmToFeet))], 200.0 * MmToFeet,
                materials.AsphaltMaterialId, comment, "ГП-ПАРК");

            bool alongX = side is BuildingSide.South or BuildingSide.North;
            double spotW = alongX ? r.W / spots : r.H / spots;
            double z = baseZ - (101.0 * MmToFeet);
            double sh = 60.0 * MmToFeet;
            double e = 150.0 * MmToFeet;
            List<GeometryObject> marks = [];

            try
            {
                for (int i = 0; i <= spots; i++)
                {
                    CurveLoop stripe = alongX
                        ? CreateRectangleLoop(r.MinX + (i * spotW) - sh, r.MinY + e, r.MinX + (i * spotW) + sh, r.MaxY - e, z)
                        : CreateRectangleLoop(r.MinX + e, r.MinY + (i * spotW) - sh, r.MaxX - e, r.MinY + (i * spotW) + sh, z);
                    marks.Add(GeometryCreationUtilities.CreateExtrusionGeometry([stripe], XYZ.BasisZ, 4.0 * MmToFeet,
                        new SolidOptions(materials.WhiteStripeMaterialId, ElementId.InvalidElementId)));

                    if (i == spots) break;

                    double c = (alongX ? r.MinX : r.MinY) + ((i + 0.5) * spotW);
                    double hl = 800.0 * MmToFeet;
                    double s0 = 650.0 * MmToFeet;
                    double s1 = 830.0 * MmToFeet;
                    CurveLoop stopper = side switch
                    {
                        BuildingSide.South => CreateRectangleLoop(c - hl, r.MinY + s0, c + hl, r.MinY + s1, baseZ - (102.0 * MmToFeet)),
                        BuildingSide.North => CreateRectangleLoop(c - hl, r.MaxY - s1, c + hl, r.MaxY - s0, baseZ - (102.0 * MmToFeet)),
                        BuildingSide.West => CreateRectangleLoop(r.MinX + s0, c - hl, r.MinX + s1, c + hl, baseZ - (102.0 * MmToFeet)),
                        _ => CreateRectangleLoop(r.MaxX - s1, c - hl, r.MaxX - s0, c + hl, baseZ - (102.0 * MmToFeet))
                    };
                    marks.Add(GeometryCreationUtilities.CreateExtrusionGeometry([stopper], XYZ.BasisZ, 120.0 * MmToFeet,
                        new SolidOptions(materials.YellowStopperMaterialId, ElementId.InvalidElementId)));
                }

                DirectShape ds = DirectShape.CreateElement(doc, siteCatId);
                ds.SetShape(marks);
                SetZoneMetadata(ds, comment + " — разметка и колесоотбойники", "ГП-РАЗМ");
            }
            catch
            {
            }
        }

        private static void CreateZoneSlabWithCurb(
            Document doc,
            ElementId siteCatId,
            Rect2 r,
            double baseZ,
            ElementId materialId,
            double slabHeightFt,
            string comment,
            string mark)
        {
            double slabBaseZ = baseZ - (250.0 * MmToFeet);
            Create3DExtrusionDirectShape(doc, siteCatId, [r.ToLoop(slabBaseZ)], slabHeightFt, materialId, comment, mark);

            double curbW = CurbWidthMm * MmToFeet;
            CurveLoop outer = r.Expand(curbW).ToLoop(baseZ - (400.0 * MmToFeet));
            CurveLoop inner = r.ToLoop(baseZ - (400.0 * MmToFeet));
            inner.Flip();
            Create3DExtrusionDirectShape(doc, siteCatId, [outer, inner], 300.0 * MmToFeet, CurbMaterialFallback(materialId),
                comment + " — бордюр", "ГП-БОРД");
        }

        // Заглушка выбора материала бордюра (всегда бетонный бордюр задается вызывающим кодом через SiteMaterials)
        [ThreadStatic]
        private static ElementId? s_curbMaterialId;

        private static ElementId CurbMaterialFallback(ElementId fallback) => s_curbMaterialId ?? fallback;

        private static void AddDirectShape(Document doc, ElementId catId, List<GeometryObject> solids, string comment, string mark)
        {
            if (solids.Count == 0) return;
            try
            {
                DirectShape ds = DirectShape.CreateElement(doc, catId);
                ds.SetShape(solids);
                SetZoneMetadata(ds, comment, mark);
            }
            catch
            {
            }
        }

        // =========================================================================
        // 5. ГЕНПЛАН РЕСТОРАНА ПО АНАЛИЗУ ДВЕРЕЙ И ПОМЕЩЕНИЙ МОДЕЛИ
        // =========================================================================

        /// <summary>
        /// Строит генплан ресторана: сквер и гостевая парковка — у главного входа (дверь в вестибюль/зал),
        /// веранда — у фасада обеденного зала, хоздвор/ТБО/парковка персонала — у служебного входа (кухня/склад),
        /// ТП — у технических помещений. Все зоны размещаются планировщиком без наложений и внутри участка.
        /// </summary>
        private static SitePlanExplicationData BuildRestaurantSiteByAnalysis(
            Document doc,
            BoundingBoxXYZ buildingBox,
            double baseZ,
            CurveLoop buildingLoop,
            CurveLoop blindAreaLoop,
            CurveLoop roadLoop,
            CurveLoop siteBoundaryLoop,
            SiteMaterials materials,
            BuildingDoorRoomAnalysis analysis,
            string buildingPurposeName,
            List<CurveLoop> extraWalkways,
            List<CurveLoop> extraDriveways,
            SitePlanEditorConfig? editorConfig,
            out int parkingSpotsCount,
            out int createdTreesCount,
            out double drivewayExtraRoadM2,
            out double walkwayExtraTileM2,
            out CurveLoop accountingParkingLoop)
        {
            ElementId siteCatId = new ElementId(BuiltInCategory.OST_Site);
            ElementId plantingCatId = new ElementId(BuiltInCategory.OST_Planting);
            s_curbMaterialId = materials.CurbConcreteMaterialId;
            drivewayExtraRoadM2 = 0.0;
            walkwayExtraTileM2 = 0.0;

            bool incTerrace = editorConfig?.IncludeTerrace ?? true;
            bool incPlaza = editorConfig?.IncludeFountainPlaza ?? true;
            bool incGuestParking = editorConfig?.IncludeGuestParking ?? true;
            bool incDock = editorConfig?.IncludeServiceDock ?? true;
            bool incWaste = editorConfig?.IncludeWasteSite ?? true;
            bool incKids = editorConfig?.IncludePlayground ?? true;
            bool incTP = editorConfig?.IncludeUtilityBlock ?? true;
            bool incStaffParking = editorConfig?.IncludeStaffParking ?? true;
            bool incFence = editorConfig?.IncludePerimeterFence ?? true;
            bool incTrees = editorConfig?.IncludePerimeterTrees ?? true;

            DrawRedSiteBoundaryWithNodes(doc, siteCatId, siteBoundaryLoop, baseZ, materials);

            // 1. 3D-основание участка: газон и озеленение территории (отметка верха -100 мм)
            Create3DExtrusionDirectShape(
                doc,
                siteCatId,
                [ShiftLoopZ(siteBoundaryLoop, baseZ - (350.0 * MmToFeet))],
                heightFt: 250.0 * MmToFeet,
                materials.GrassMaterialId,
                "ГП_3D: Газон и озеленение территории (+15000 мм)",
                "ГП-ГАЗ");

            // 2. 3D-дорожное полотно: кольцевой проезд вокруг здания (отметка верха -250 мм)
            CurveLoop roadOutZ = ShiftLoopZ(roadLoop, baseZ - (450.0 * MmToFeet));
            CurveLoop roadInZ = ShiftLoopZ(blindAreaLoop, baseZ - (450.0 * MmToFeet));
            roadInZ.Flip();

            Create3DExtrusionDirectShape(
                doc,
                siteCatId,
                [roadOutZ, roadInZ],
                heightFt: 200.0 * MmToFeet,
                materials.AsphaltMaterialId,
                "ГП_3D: Кольцевой проезд и автодорога — асфальт (+6000 мм)",
                "ГП-ДОР");

            // 3. 3D-отмостка и тротуар вокруг здания (отметка верха -100 мм)
            CurveLoop curbInnerLoop = CurveLoop.CreateViaOffset(
                buildingLoop,
                (BlindAreaOffsetMm - CurbWidthMm) * MmToFeet,
                XYZ.BasisZ);

            CurveLoop blindOutZ = ShiftLoopZ(curbInnerLoop, baseZ - (250.0 * MmToFeet));
            CurveLoop blindInZ = ShiftLoopZ(buildingLoop, baseZ - (250.0 * MmToFeet));
            blindInZ.Flip();

            Create3DExtrusionDirectShape(
                doc,
                siteCatId,
                [blindOutZ, blindInZ],
                heightFt: 150.0 * MmToFeet,
                materials.PavingTileMaterialId,
                "ГП_3D: Отмостка и тротуары — тротуарная плитка (+1500 мм)",
                "ГП-ТРОТ");

            // 4. 3D-бордюрный камень БР 100.30.15 (верх на -100 мм, вровень с тротуаром)
            CurveLoop innerCurbOut = ShiftLoopZ(blindAreaLoop, baseZ - (400.0 * MmToFeet));
            CurveLoop innerCurbIn = ShiftLoopZ(curbInnerLoop, baseZ - (400.0 * MmToFeet));
            innerCurbIn.Flip();

            Create3DExtrusionDirectShape(
                doc,
                siteCatId,
                [innerCurbOut, innerCurbIn],
                heightFt: 300.0 * MmToFeet,
                materials.CurbConcreteMaterialId,
                "ГП_3D: Бордюрный камень БР 100.30.15 (внутренний)",
                "ГП-БОРД-1");

            CurveLoop roadOuterCurbLoop = CurveLoop.CreateViaOffset(
                buildingLoop,
                (RoadOffsetMm + CurbWidthMm) * MmToFeet,
                XYZ.BasisZ);

            CurveLoop outerCurbOut = ShiftLoopZ(roadOuterCurbLoop, baseZ - (400.0 * MmToFeet));
            CurveLoop outerCurbIn = ShiftLoopZ(roadLoop, baseZ - (400.0 * MmToFeet));
            outerCurbIn.Flip();

            Create3DExtrusionDirectShape(
                doc,
                siteCatId,
                [outerCurbOut, outerCurbIn],
                heightFt: 300.0 * MmToFeet,
                materials.CurbConcreteMaterialId,
                "ГП_3D: Бордюрный камень БР 100.30.15 (внешний)",
                "ГП-БОРД-2");

            Rect2 building = new Rect2(buildingBox.Min.X, buildingBox.Min.Y, buildingBox.Max.X, buildingBox.Max.Y);
            Rect2 ring = building.Expand((RoadOffsetMm + CurbWidthMm) * MmToFeet);
            List<XYZ> sitePoly = siteBoundaryLoop.Select(c => c.GetEndPoint(0)).ToList();
            SiteLayoutPlanner planner = new SiteLayoutPlanner(ring, sitePoly, 1.0 * OneMeter, 1.5 * OneMeter);

            // --- Входы по анализу модели ---
            SiteDoorInfo mainDoor = analysis.MainDoor
                ?? CreateSyntheticDoor(building, BuildingSide.South, baseZ, "главный вход (дверь не найдена)");
            SiteDoorInfo serviceDoor = analysis.ServiceDoor
                ?? CreateSyntheticDoor(building, OppositeSide(mainDoor.Side), baseZ, "служебный вход (дверь не найдена)");

            SiteRoomInfo? hallRoom = analysis.Rooms.Where(r => r.Kind == RoomKind.Hall).OrderByDescending(r => r.AreaM2).FirstOrDefault();
            SiteDoorInfo? hallDoor = analysis.ExteriorDoors
                .Where(d => d.Kind == RoomKind.Hall && !ReferenceEquals(d, mainDoor))
                .OrderByDescending(d => d.WidthM)
                .FirstOrDefault();
            SiteRoomInfo? techRoom = analysis.Rooms.Where(r => r.Kind == RoomKind.Technical).OrderByDescending(r => r.AreaM2).FirstOrDefault();

            (BuildingSide terrSide, XYZ terrAnchor) = hallDoor != null
                ? (hallDoor.Side, hallDoor.FacadePoint)
                : hallRoom != null
                    ? NearestFacade(building, hallRoom.Center, baseZ)
                    : (mainDoor.Side, mainDoor.FacadePoint);

            (BuildingSide techSide, XYZ techAnchor) = techRoom != null
                ? NearestFacade(building, techRoom.Center, baseZ)
                : (serviceDoor.Side, serviceDoor.FacadePoint);

            double buildingAreaM2 = CalculateCurveLoopAreaM2(buildingLoop);
            double hallAreaM2 = analysis.Rooms.Where(r => r.Kind == RoomKind.Hall).Sum(r => r.AreaM2);
            int seats = hallAreaM2 > 1.0
                ? Math.Max(20, (int)Math.Round(hallAreaM2 / 1.8))
                : Math.Max(50, (int)Math.Round(buildingAreaM2 / 4.5));

            // --- Пешеходные подходы от реальных дверей ---
            walkwayExtraTileM2 += CreateDoorApproach(doc, siteCatId, mainDoor, building, ring, 2400.0 * MmToFeet, baseZ, materials,
                "главного входа", extraWalkways);
            if (!ReferenceEquals(serviceDoor, mainDoor))
            {
                walkwayExtraTileM2 += CreateDoorApproach(doc, siteCatId, serviceDoor, building, ring, 1800.0 * MmToFeet, baseZ, materials,
                    "служебного входа", extraWalkways);
            }

            // === № 3: Входная группа со сквером и фонтаном — напротив главного входа ===
            Rect2? plaza = null;
            double plazaAreaM2 = 0.0;
            if (incPlaza)
            {
                plaza = planner.Place(mainDoor.FacadePoint, mainDoor.Side, 12.0 * OneMeter, 8.0 * OneMeter, 0.0);
                if (plaza is Rect2 pz)
                {
                    CreateZoneSlabWithCurb(doc, siteCatId, pz, baseZ, materials.PavingTileMaterialId, 150.0 * MmToFeet,
                        "ГП_3D: Входная группа со сквером и фонтаном", "3");
                    XYZ fountainPt = pz.Center(baseZ - (100.0 * MmToFeet));
                    double fr = Math.Min(2200.0 * MmToFeet, Math.Min(pz.W, pz.H) / 3.2);
                    AddDirectShape(doc, siteCatId, CreateFountainSolid(fountainPt, fr, materials), "ГП_3D: Фонтан входного сквера", "ГП-МАФ");

                    List<GeometryObject> benches = [];
                    bool horiz = mainDoor.Side is BuildingSide.South or BuildingSide.North;
                    if (horiz)
                    {
                        benches.AddRange(CreateBenchSolid(new XYZ(pz.MinX + (1200.0 * MmToFeet), pz.CenterY, baseZ - (100.0 * MmToFeet)), Math.PI / 2.0, materials));
                        benches.AddRange(CreateBenchSolid(new XYZ(pz.MaxX - (1200.0 * MmToFeet), pz.CenterY, baseZ - (100.0 * MmToFeet)), -Math.PI / 2.0, materials));
                    }
                    else
                    {
                        benches.AddRange(CreateBenchSolid(new XYZ(pz.CenterX, pz.MinY + (1200.0 * MmToFeet), baseZ - (100.0 * MmToFeet)), 0.0, materials));
                        benches.AddRange(CreateBenchSolid(new XYZ(pz.CenterX, pz.MaxY - (1200.0 * MmToFeet), baseZ - (100.0 * MmToFeet)), Math.PI, materials));
                    }
                    AddDirectShape(doc, siteCatId, benches, "ГП_3D: Скамейки входного сквера", "ГП-МАФ");

                    plazaAreaM2 = pz.AreaM2;
                    extraWalkways.Add(pz.ToLoop(baseZ));
                }
            }

            // === № 2: Летняя веранда — у фасада обеденного зала (если включена в редакторе) ===
            Rect2? terrace = null;
            double terraceAreaM2 = 0.0;
            if (incTerrace)
            {
                double sideLen = terrSide is BuildingSide.South or BuildingSide.North ? building.W : building.H;
                double terrAlong = Math.Clamp(sideLen * 0.6, 9.0 * OneMeter, 16.0 * OneMeter);
                terrace = planner.Place(terrAnchor, terrSide, terrAlong, 7.0 * OneMeter, 0.0);
                if (terrace is Rect2 tr)
                {
                    CreateZoneSlabWithCurb(doc, siteCatId, tr, baseZ, materials.WoodBenchMaterialId, 190.0 * MmToFeet,
                        "ГП_3D: Летняя веранда ресторана у обеденного зала", "2");

                    List<GeometryObject> dining = [];
                    double sp = 3200.0 * MmToFeet;
                    for (double tx = tr.MinX + (1700.0 * MmToFeet); tx <= tr.MaxX - (1700.0 * MmToFeet); tx += sp)
                    {
                        for (double ty = tr.MinY + (1700.0 * MmToFeet); ty <= tr.MaxY - (1700.0 * MmToFeet); ty += sp)
                        {
                            dining.AddRange(CreateDiningTableWithChairsSolid(new XYZ(tx, ty, baseZ - (60.0 * MmToFeet)), materials));
                        }
                    }
                    AddDirectShape(doc, siteCatId, dining, "ГП_3D: Столики летней веранды", "ГП-МАФ");

                    double terrAlongCoord = terrSide is BuildingSide.South or BuildingSide.North ? tr.CenterX : tr.CenterY;
                    CreateRingCrossing(doc, siteCatId, terrSide, terrAlongCoord, building, ring, 3000.0 * MmToFeet, baseZ, materials,
                        "ГП_3D: Пешеходный переход от зала к веранде");

                    terraceAreaM2 = tr.AreaM2;
                }
            }

            // === № 7: Детский уголок — рядом с верандой (если включен в редакторе) ===
            Rect2? kids = null;
            double kidsAreaM2 = 0.0;
            if (incKids)
            {
                XYZ kidsAnchor = terrace?.Center(baseZ) ?? terrAnchor;
                kids = planner.Place(kidsAnchor, terrSide, 7.5 * OneMeter, 6.0 * OneMeter, 0.0);
                if (kids is Rect2 kr)
                {
                    CreateZoneSlabWithCurb(doc, siteCatId, kr, baseZ, materials.PlaygroundYellowMaterialId, 150.0 * MmToFeet,
                        "ГП_3D: Детский игровой уголок ресторана", "7");
                    List<GeometryObject> maf = [];
                    maf.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                        [CreateRectangleLoop(kr.MinX + (700.0 * MmToFeet), kr.MinY + (700.0 * MmToFeet), kr.MinX + (2900.0 * MmToFeet), kr.MinY + (2900.0 * MmToFeet), baseZ - (100.0 * MmToFeet))],
                        XYZ.BasisZ, 250.0 * MmToFeet, new SolidOptions(materials.WoodBenchMaterialId, ElementId.InvalidElementId)));
                    maf.AddRange(CreateBenchSolid(new XYZ(kr.MaxX - (800.0 * MmToFeet), kr.CenterY, baseZ - (100.0 * MmToFeet)), Math.PI / 2.0, materials));
                    AddDirectShape(doc, siteCatId, maf, "ГП_3D: Песочница и скамейка детского уголка", "ГП-МАФ");
                    kidsAreaM2 = kr.AreaM2;
                }
            }

            // === № 4: Гостевая автостоянка — у главного входа (если включена в редакторе) ===
            int guestSpots = 0;
            Rect2? guest = null;
            double guestAreaM2 = 0.0;
            if (incGuestParking)
            {
                guestSpots = Math.Clamp((int)Math.Round(seats / 5.0), 6, 14);
                for (int n = guestSpots; n >= 4 && guest == null; n -= 2)
                {
                    guest = planner.Place(mainDoor.FacadePoint, mainDoor.Side, n * ParkingSpotWidthMm * MmToFeet, ParkingSpotLengthMm * MmToFeet, 0.0);
                    if (guest != null) guestSpots = n;
                }

                if (guest is Rect2 gr)
                {
                    BuildingSide gSide = SideOfRectRelativeToRing(gr, ring);
                    int fitted = Math.Max(1, (int)Math.Floor(((gSide is BuildingSide.South or BuildingSide.North) ? gr.W : gr.H) / (ParkingSpotWidthMm * MmToFeet) + 1e-6));
                    guestSpots = fitted;
                    CreateParkingLotOnRect(doc, siteCatId, gr, gSide, guestSpots, baseZ, materials, "ГП_3D: Гостевая автостоянка у главного входа");
                    guestAreaM2 = gr.AreaM2;
                    accountingParkingLoop = gr.ToLoop(baseZ);
                }
                else
                {
                    guestSpots = 0;
                    accountingParkingLoop = CreateRectangleLoop(ring.MinX, ring.MinY - OneMeter, ring.MinX + OneMeter, ring.MinY, baseZ);
                }
            }
            else
            {
                guestSpots = 0;
                accountingParkingLoop = CreateRectangleLoop(ring.MinX, ring.MinY - OneMeter, ring.MinX + OneMeter, ring.MinY, baseZ);
            }

            // --- Въезд с улицы: со стороны главного входа, рядом с гостевой стоянкой ---
            XYZ mainDrvAnchor = guest?.Center(baseZ) ?? mainDoor.FacadePoint;
            Rect2? mainDrive = planner.PlaceDriveway(mainDoor.Side, mainDrvAnchor, 6000.0 * MmToFeet)
                               ?? planner.PlaceDriveway(OppositeSide(serviceDoor.Side), mainDrvAnchor, 6000.0 * MmToFeet);
            if (mainDrive is Rect2 md)
            {
                CurveLoop loop = md.ToLoop(baseZ - (450.0 * MmToFeet));
                Create3DExtrusionDirectShape(doc, siteCatId, [loop], 200.0 * MmToFeet, materials.AsphaltMaterialId,
                    "ГП_3D: Въезд на участок с улицы (к гостевой стоянке)", "ГП-ВЪЕЗД");
                drivewayExtraRoadM2 += md.AreaM2;
                extraDriveways.Add(md.ToLoop(baseZ));
            }

            // === № 5: Хоздвор и дебаркадер кухни — у служебного входа (если включен в редакторе) ===
            Rect2? dock = null;
            double dockAreaM2 = 0.0;
            if (incDock)
            {
                dock = planner.Place(serviceDoor.FacadePoint, serviceDoor.Side, 9.0 * OneMeter, 7.0 * OneMeter, 0.0);
                if (dock is Rect2 dr)
                {
                    Create3DExtrusionDirectShape(doc, siteCatId, [dr.ToLoop(baseZ - (450.0 * MmToFeet))], 200.0 * MmToFeet, materials.AsphaltMaterialId,
                        $"ГП_3D: Хоздвор и дебаркадер у служебного входа «{serviceDoor.RoomName}»", "5");
                    AddDirectShape(doc, siteCatId, CreateLoadingDockRampSolid(dr.MinX, dr.MinY, dr.MaxX, dr.MaxY, baseZ - (250.0 * MmToFeet), materials),
                        "ГП_3D: Разгрузочная рампа и навес кухни", "ГП-МАФ");
                    dockAreaM2 = dr.AreaM2;
                }
            }

            XYZ serviceAnchor = dock?.Center(baseZ) ?? serviceDoor.FacadePoint;

            // === № 6: Площадка ТБО — у хоздвора (если включена в редакторе) ===
            Rect2? waste = null;
            double wasteAreaM2 = 0.0;
            if (incWaste)
            {
                waste = planner.Place(serviceAnchor, serviceDoor.Side, 5.5 * OneMeter, 3.5 * OneMeter, 0.0);
                if (waste is Rect2 wr)
                {
                    Create3DExtrusionDirectShape(doc, siteCatId, [wr.ToLoop(baseZ - (250.0 * MmToFeet))], 150.0 * MmToFeet, materials.WasteSiteMaterialId,
                        "ГП_3D: Площадка ТБО ресторана", "6");
                    AddDirectShape(doc, siteCatId, CreateWasteEnclosureAndBins(wr.MinX, wr.MinY, wr.MaxX, wr.MaxY, baseZ - (100.0 * MmToFeet), materials),
                        "ГП_3D: Ограждение и контейнеры ТБО", "ГП-МАФ");
                    wasteAreaM2 = wr.AreaM2;
                }
            }

            // === № 9: Служебная автостоянка персонала — у служебного входа (если включена в редакторе) ===
            int staffSpots = 0;
            Rect2? staff = null;
            double staffAreaM2 = 0.0;
            if (incStaffParking)
            {
                staffSpots = 4;
                staff = planner.Place(serviceAnchor, serviceDoor.Side, staffSpots * ParkingSpotWidthMm * MmToFeet, ParkingSpotLengthMm * MmToFeet, 0.0);
                if (staff is Rect2 sr)
                {
                    BuildingSide sSide = SideOfRectRelativeToRing(sr, ring);
                    staffSpots = Math.Max(1, (int)Math.Floor(((sSide is BuildingSide.South or BuildingSide.North) ? sr.W : sr.H) / (ParkingSpotWidthMm * MmToFeet) + 1e-6));
                    CreateParkingLotOnRect(doc, siteCatId, sr, sSide, staffSpots, baseZ, materials, "ГП_3D: Служебная автостоянка персонала");
                    staffAreaM2 = sr.AreaM2;
                }
                else
                {
                    staffSpots = 0;
                }
            }

            // --- Хозяйственный въезд (если служебный вход на другом фасаде) ---
            if (serviceDoor.Side != mainDoor.Side)
            {
                Rect2? sDrive = planner.PlaceDriveway(serviceDoor.Side, serviceAnchor, 4500.0 * MmToFeet);
                if (sDrive is Rect2 sd)
                {
                    Create3DExtrusionDirectShape(doc, siteCatId, [sd.ToLoop(baseZ - (450.0 * MmToFeet))], 200.0 * MmToFeet, materials.AsphaltMaterialId,
                        "ГП_3D: Хозяйственный въезд к служебному входу", "ГП-ВЪЕЗД");
                    drivewayExtraRoadM2 += sd.AreaM2;
                    extraDriveways.Add(sd.ToLoop(baseZ));
                }
            }

            // === № 8: Инженерный блок (ТП / хладоцентр) — у технических помещений (если включен в редакторе) ===
            Rect2? tp = null;
            double utilityAreaM2 = 0.0;
            if (incTP)
            {
                tp = planner.Place(techAnchor, techSide, 4.5 * OneMeter, 3.5 * OneMeter, 1.0 * OneMeter);
                if (tp is Rect2 ur)
                {
                    AddDirectShape(doc, siteCatId, CreateSubstationBuildingSolid(ur.MinX, ur.MinY, ur.MaxX, ur.MaxY, baseZ - (100.0 * MmToFeet), materials),
                        "ГП_3D: Инженерный блок ТП / хладоцентр", "8");
                    utilityAreaM2 = ur.AreaM2;
                }
            }

            // --- Озеленение: деревья по периметру участка в свободных местах + освещение у входа ---
            List<XYZ> treePts = [];
            if (incTrees)
            {
                for (int i = 0; i < sitePoly.Count; i++)
                {
                    XYZ p0 = sitePoly[i];
                    XYZ p1 = sitePoly[(i + 1) % sitePoly.Count];
                    XYZ d = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0.0);
                    double len = d.GetLength();
                    if (len < 4.0 * OneMeter) continue;
                    d = d / len;
                    XYZ inward = new XYZ(-d.Y, d.X, 0.0);
                    for (double t = 3.0 * OneMeter; t < len - (3.0 * OneMeter); t += 7.0 * OneMeter)
                    {
                        XYZ pos = new XYZ(p0.X + (d.X * t) + (inward.X * 2.5 * OneMeter), p0.Y + (d.Y * t) + (inward.Y * 2.5 * OneMeter), baseZ - (100.0 * MmToFeet));
                        if (!planner.InsideSite(pos.X, pos.Y)) continue;
                        if (!planner.IsFree(pos, 2.2 * OneMeter)) continue;
                        if (extraDriveways.Any(l => LoopBoxContains(l, pos, 2.5 * OneMeter))) continue;
                        treePts.Add(pos);
                    }
                }

                List<GeometryObject> treeSolids = [];
                foreach (XYZ p in treePts)
                {
                    treeSolids.AddRange(CreateTreeSolid(p, materials));
                }
                AddDirectShape(doc, plantingCatId, treeSolids, "ГП_3D: Деревья по периметру участка ресторана", "ГП-ДЕРЕВЬЯ");
            }
            createdTreesCount = treePts.Count;

            if (plaza is Rect2 pl)
            {
                List<GeometryObject> lamps = [];
                lamps.AddRange(CreateLampSolid(new XYZ(pl.MinX + (400.0 * MmToFeet), pl.MinY + (400.0 * MmToFeet), baseZ), 1.0, 1.0, materials));
                lamps.AddRange(CreateLampSolid(new XYZ(pl.MaxX - (400.0 * MmToFeet), pl.MaxY - (400.0 * MmToFeet), baseZ), -1.0, -1.0, materials));
                AddDirectShape(doc, siteCatId, lamps, "ГП_3D: Освещение входной группы", "ГП-МАФ");
            }

            // --- Баланс территории и расчеты ---
            double totalSiteAreaM2 = CalculateCurveLoopAreaM2(siteBoundaryLoop);
            double blindAreaM2 = Math.Max(0.0, CalculateCurveLoopAreaM2(blindAreaLoop) - buildingAreaM2) + walkwayExtraTileM2;
            double roadAreaM2 = Math.Max(0.0, CalculateCurveLoopAreaM2(roadLoop) - CalculateCurveLoopAreaM2(blindAreaLoop)) + drivewayExtraRoadM2;
            double sumConstructed = buildingAreaM2 + blindAreaM2 + roadAreaM2 + guestAreaM2 + staffAreaM2 +
                                    terraceAreaM2 + plazaAreaM2 + dockAreaM2 + wasteAreaM2 + kidsAreaM2 + utilityAreaM2;
            double greenM2 = Math.Max(0.0, totalSiteAreaM2 - sumConstructed);

            double dailyWasteM3 = (seats * 1.45 * 1.20) / 365.0;
            int wasteBins = Math.Max(2, (int)Math.Ceiling(dailyWasteM3 / (0.9 * 0.75)));
            parkingSpotsCount = guestSpots + staffSpots;

            string hallName = hallRoom?.Name ?? "зал";
            string techName = techRoom?.Name ?? serviceDoor.RoomName;

            // ДИНАМИЧЕСКАЯ РЕГИСТРАЦИЯ МАРОК И ФОРМИРОВАНИЕ ТАБЛИЦЫ 1 (без пропусков в нумерации)
            int markNumber = 1;
            List<string[]> t1 = [];

            // 1. Проектируемое здание
            RegisterPlanMarker(building.Center(baseZ), markNumber);
            t1.Add([markNumber.ToString(), $"Проектируемое здание ({buildingPurposeName})", $"Застройка {buildingAreaM2:F1} м²; гл. вход — «{mainDoor.RoomName}»"]);
            markNumber++;

            // 2. Открытая летняя веранда (терраса) — если включена в редакторе
            if (incTerrace && terrace is Rect2 trActive)
            {
                RegisterPlanMarker(trActive.Center(baseZ), markNumber);
                t1.Add([markNumber.ToString(), "Открытая летняя веранда (терраса)", $"У фасада «{hallName}», {terraceAreaM2:F1} м²"]);
                markNumber++;
            }

            // 3. Входная группа со сквером и фонтаном — если включена
            if (incPlaza && plaza is Rect2 pzActive)
            {
                bool horiz = mainDoor.Side is BuildingSide.South or BuildingSide.North;
                XYZ markPt = horiz
                    ? new XYZ(pzActive.MinX + (pzActive.W * 0.22), pzActive.CenterY, baseZ)
                    : new XYZ(pzActive.CenterX, pzActive.MinY + (pzActive.H * 0.22), baseZ);
                RegisterPlanMarker(markPt, markNumber);
                t1.Add([markNumber.ToString(), "Входная группа со сквером и фонтаном", $"Напротив главного входа, {plazaAreaM2:F1} м²"]);
                markNumber++;
            }

            // 4. Гостевая автостоянка — если включена
            if (incGuestParking && guest is Rect2 grActive)
            {
                RegisterPlanMarker(grActive.Center(baseZ), markNumber);
                t1.Add([markNumber.ToString(), "Гостевая автостоянка", $"{guestSpots} м/м у главного входа, {guestAreaM2:F1} м²"]);
                markNumber++;
            }

            // 5. Хозяйственный двор и дебаркадер кухни — если включен
            if (incDock && dock is Rect2 drActive)
            {
                RegisterPlanMarker(drActive.Center(baseZ), markNumber);
                t1.Add([markNumber.ToString(), "Хозяйственный двор и дебаркадер кухни", $"У служебного входа «{serviceDoor.RoomName}», {dockAreaM2:F1} м²"]);
                markNumber++;
            }

            // 6. Площадка ТБО — если включена
            if (incWaste && waste is Rect2 wrActive)
            {
                RegisterPlanMarker(wrActive.Center(baseZ), markNumber);
                t1.Add([markNumber.ToString(), "Площадка ТБО", $"{wasteBins} конт. по 0.75 м³, {wasteAreaM2:F1} м²"]);
                markNumber++;
            }

            // 7. Детский игровой уголок — если включен
            if (incKids && kids is Rect2 krActive)
            {
                RegisterPlanMarker(krActive.Center(baseZ), markNumber);
                t1.Add([markNumber.ToString(), "Детский игровой уголок", $"Рядом с верандой, {kidsAreaM2:F1} м²"]);
                markNumber++;
            }

            // 8. Инженерный блок (ТП / хладоцентр) — если включен
            if (incTP && tp is Rect2 urActive)
            {
                RegisterPlanMarker(urActive.Center(baseZ), markNumber);
                t1.Add([markNumber.ToString(), "Инженерный блок (ТП / хладоцентр)", $"У помещения «{techName}», {utilityAreaM2:F1} м²"]);
                markNumber++;
            }

            // 9. Служебная автостоянка персонала — если включена
            if (incStaffParking && staff is Rect2 srActive)
            {
                RegisterPlanMarker(srActive.Center(baseZ), markNumber);
                t1.Add([markNumber.ToString(), "Служебная автостоянка персонала", $"{staffSpots} м/м у служебного входа, {staffAreaM2:F1} м²"]);
                markNumber++;
            }

            // ДИНАМИЧЕСКОЕ ФОРМИРОВАНИЕ ТАБЛИЦЫ 2 (Расчет площадок)
            int t2Num = 1;
            List<string[]> t2 = [];
            if (incTerrace && terraceAreaM2 > 0.01)
            {
                t2.Add([$"{t2Num++}. Летняя веранда", "0.5", seats.ToString(), $"{seats * 0.5:F1}", $"{terraceAreaM2:F1}"]);
            }
            if (incPlaza && plazaAreaM2 > 0.01)
            {
                t2.Add([$"{t2Num++}. Входной сквер", "0.8", seats.ToString(), $"{seats * 0.8:F1}", $"{plazaAreaM2:F1}"]);
            }
            if ((incGuestParking && guestAreaM2 > 0.01) || (incStaffParking && staffAreaM2 > 0.01))
            {
                t2.Add([$"{t2Num++}. Автостоянки", "1.2", seats.ToString(), $"{seats * 1.2:F1}", $"{guestAreaM2 + staffAreaM2:F1}"]);
            }
            if ((incDock && dockAreaM2 > 0.01) || (incWaste && wasteAreaM2 > 0.01))
            {
                t2.Add([$"{t2Num++}. Хоздвор и ТБО", "0.4", seats.ToString(), $"{seats * 0.4:F1}", $"{dockAreaM2 + wasteAreaM2:F1}"]);
            }
            if (incKids && kidsAreaM2 > 0.01)
            {
                t2.Add([$"{t2Num++}. Детский уголок", "0.2", seats.ToString(), $"{seats * 0.2:F1}", $"{kidsAreaM2:F1}"]);
            }
            t2.Add([$"{t2Num++}. Озеленение", "1.5", seats.ToString(), $"{seats * 1.5:F1}", $"{greenM2:F1}"]);

            bool byHall = hallAreaM2 > 1.0;
            return new SitePlanExplicationData(
                BuildingPurposeName: buildingPurposeName,
                PurposeCategory: BuildingPurposeCategory.Restaurant,
                Table1Rows: t1,
                Table2Rows: t2,
                Calc1Header: "Расчет вместимости ресторана",
                Calc1Formula: byHall ? "S зала / 1.8 = N (посадочных мест)" : "S / 4.5 = N (посадочных мест)",
                Calc1Desc: byHall
                    ? "где: S зала — площадь обеденных залов по помещениям модели, 1.8 м²/место"
                    : "где: S — площадь застройки, 4.5 м²/место (залы в модели не найдены)",
                Calc1Result: byHall
                    ? $"{hallAreaM2:F1} / 1.8 = {seats} посадочных мест"
                    : $"{buildingAreaM2:F1} / 4.5 = {seats} посадочных мест",
                Calc2Header: "Расчет ТБО предприятия питания",
                Calc2Formula: "M = (N × 1.45 кг × 1.2) / 365",
                Calc2Result: $"Суточное накопление: {dailyWasteM3:F2} м³/сут ({wasteBins} конт. по 0.75 м³)",
                BuildingAreaM2: buildingAreaM2,
                RoadAreaM2: roadAreaM2,
                BlindAreaM2: blindAreaM2,
                ParkingAreaM2: guestAreaM2 + staffAreaM2,
                GreenAreaM2: greenM2,
                TotalSiteAreaM2: totalSiteAreaM2,
                ParkingSpotsCount: parkingSpotsCount);
        }

        private static BuildingSide SideOfRectRelativeToRing(Rect2 r, Rect2 ring)
        {
            if (r.MaxY <= ring.MinY + 1e-4) return BuildingSide.South;
            if (r.MinY >= ring.MaxY - 1e-4) return BuildingSide.North;
            if (r.MaxX <= ring.MinX + 1e-4) return BuildingSide.West;
            return BuildingSide.East;
        }

        private static bool LoopBoxContains(CurveLoop loop, XYZ p, double pad)
        {
            List<XYZ> pts = GetLoopVertices(loop);
            return p.X >= pts.Min(q => q.X) - pad && p.X <= pts.Max(q => q.X) + pad &&
                   p.Y >= pts.Min(q => q.Y) - pad && p.Y <= pts.Max(q => q.Y) + pad;
        }
    }
}
