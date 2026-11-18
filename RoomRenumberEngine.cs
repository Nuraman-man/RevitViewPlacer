using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitViewPlacer
{
    /// <summary>
    /// Результат выполнения упорядочения и перенумерации помещений по ГОСТ.
    /// </summary>
    public sealed record RoomRenumberResult(
        int RenumberedCount,
        int LevelsCount,
        int IsolatedUnplacedCount,
        string Message);

    /// <summary>
    /// Движок интеллектуального упорядочения и перенумерации помещений по ГОСТ 21.501-93 и СН РК.
    /// Исключает пропуски, устраняет хаотичную нумерацию и упорядочивает помещения
    /// по логическому ходу движения от главного входа / тамбура (1, 2, 3...).
    /// </summary>
    public static class RoomRenumberEngine
    {
        public const string ModePerFloor = "PerFloor";
        public const string ModeContinuous = "Continuous";
        public const string ModeFloorPrefixed = "FloorPrefixed";

        public static readonly string[] NumberingModeLabels =
        [
            "По этажам (1, 2, 3... на каждом этаже)",
            "Сквозная (1, 2, 3... через все здание)",
            "С префиксом этажа (101, 102... / 201, 202...)"
        ];

        /// <summary>
        /// Выполняет упорядочение и перенумерацию всех размещенных помещений в проекте.
        /// </summary>
        public static RoomRenumberResult RenumberAllPlacedRooms(Document doc, string mode = ModePerFloor)
        {
            // 1. Изолируем любые неразмещенные помещения (Not Placed / Not Enclosed), чтобы они не конфликтовали с номерами
            int isolatedUnplacedCount = IsolateUnplacedRooms(doc);

            // 2. Собираем все размещенные помещения с вычисленной площадью
            List<Room> allPlacedRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => r.Area > 0.001 && r.Location != null)
                .ToList();

            if (allPlacedRooms.Count == 0)
            {
                return new RoomRenumberResult(0, 0, isolatedUnplacedCount, "В проекте не найдено размещенных помещений для нумерации.");
            }

            // 3. Находим помещения с наружными дверями (для приоритетного поиска главного входа)
            HashSet<ElementId> exteriorDoorRoomIds = FindRoomsWithExteriorDoors(doc);

            // 4. Группируем помещения по уровням (этажам) и сортируем уровни по отметке (высоте) снизу вверх
            Dictionary<ElementId, Level> levelsMap = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToDictionary(lvl => lvl.Id);

            var roomsByLevel = allPlacedRooms
                .GroupBy(r => r.LevelId)
                .OrderBy(g => levelsMap.TryGetValue(g.Key, out Level? lvl) ? lvl.Elevation : 0.0)
                .ToList();

            int totalRenumbered = 0;
            int floorIndex = 1;
            int continuousNumber = 1;

            // 5. Обрабатываем каждый этаж
            foreach (var levelGroup in roomsByLevel)
            {
                List<Room> roomsOnLevel = levelGroup.ToList();
                if (roomsOnLevel.Count == 0) continue;

                // Упорядочиваем помещения на этаже по ходу движения от входа
                List<Room> orderedRooms = OrderRoomsCirculationSequence(roomsOnLevel, exteriorDoorRoomIds);

                // Определяем начальный номер и префикс в зависимости от выбранного режима
                int startNumber;
                string prefix = string.Empty;

                switch (mode)
                {
                    case ModeContinuous:
                        startNumber = continuousNumber;
                        continuousNumber += orderedRooms.Count;
                        break;

                    case ModeFloorPrefixed:
                        // Для 1 этажа: 101, для 2 этажа: 201 и т.д.
                        startNumber = (floorIndex * 100) + 1;
                        break;

                    case ModePerFloor:
                    default:
                        startNumber = 1;
                        break;
                }

                int renumberedOnFloor = ApplyTwoPassRenumbering(doc, orderedRooms, startNumber, prefix);
                totalRenumbered += renumberedOnFloor;
                floorIndex++;
            }

            doc.Regenerate();

            string modeDesc = mode switch
            {
                ModeContinuous => "сквозная нумерация 1..N",
                ModeFloorPrefixed => "нумерация с кодом этажа (101, 201...)",
                _ => "поэтажная нумерация 1..N"
            };

            string message = $"Успешно упорядочено и перенумеровано {totalRenumbered} помещений на {roomsByLevel.Count} этаж(ах).\n" +
                             $"Режим: {modeDesc} по ходу движения от входа.\n" +
                             (isolatedUnplacedCount > 0 ? $"Неразмещенные помещения (Not Placed): изолировано {isolatedUnplacedCount} шт." : string.Empty);

            return new RoomRenumberResult(totalRenumbered, roomsByLevel.Count, isolatedUnplacedCount, message);
        }

        /// <summary>
        /// Упорядочивает список помещений одного этажа в непрерывную цепочку по ходу движения
        /// от главного входа / тамбура с использованием алгоритма ближайшего соседа (Nearest Neighbor).
        /// </summary>
        private static List<Room> OrderRoomsCirculationSequence(
            List<Room> roomsOnLevel,
            HashSet<ElementId> exteriorDoorRoomIds)
        {
            if (roomsOnLevel.Count <= 1)
            {
                return new List<Room>(roomsOnLevel);
            }

            // Находим начальное помещение этажа (главный вход / тамбур / вестибюль / холл)
            Room startRoom = FindStartingEntranceRoom(roomsOnLevel, exteriorDoorRoomIds);

            List<Room> ordered = new List<Room>(roomsOnLevel.Count) { startRoom };
            HashSet<ElementId> visited = new HashSet<ElementId> { startRoom.Id };

            XYZ currentCenter = GetRoomCenter(startRoom);

            while (ordered.Count < roomsOnLevel.Count)
            {
                Room? nextRoom = null;
                double bestDistSq = double.MaxValue;

                foreach (Room candidate in roomsOnLevel)
                {
                    if (visited.Contains(candidate.Id)) continue;

                    XYZ candCenter = GetRoomCenter(candidate);
                    double dx = currentCenter.X - candCenter.X;
                    double dy = currentCenter.Y - candCenter.Y;
                    double distSq = (dx * dx) + (dy * dy);

                    // Если расстояния практически равны, отдаем предпочтение чтению сверху-вниз, слева-направо
                    if (distSq < bestDistSq - 0.0001)
                    {
                        bestDistSq = distSq;
                        nextRoom = candidate;
                    }
                    else if (Math.Abs(distSq - bestDistSq) <= 0.0001 && nextRoom != null)
                    {
                        XYZ prevNextCenter = GetRoomCenter(nextRoom);
                        if (candCenter.Y > prevNextCenter.Y || (Math.Abs(candCenter.Y - prevNextCenter.Y) < 0.1 && candCenter.X < prevNextCenter.X))
                        {
                            nextRoom = candidate;
                        }
                    }
                }

                if (nextRoom == null) break;

                ordered.Add(nextRoom);
                visited.Add(nextRoom.Id);
                currentCenter = GetRoomCenter(nextRoom);
            }

            // Добавляем любые оставшиеся помещения, если они не были посещены
            foreach (Room r in roomsOnLevel)
            {
                if (!visited.Contains(r.Id))
                {
                    ordered.Add(r);
                }
            }

            return ordered;
        }

        /// <summary>
        /// Интеллектуальный поиск начального входного помещения этажа
        /// по наименованию (ГОСТ) и наружным дверям в наружных стенах.
        /// </summary>
        private static Room FindStartingEntranceRoom(
            List<Room> roomsOnLevel,
            HashSet<ElementId> exteriorDoorRoomIds)
        {
            Room? bestRoom = null;
            int bestScore = int.MinValue;

            foreach (Room room in roomsOnLevel)
            {
                int score = 0;
                string name = (room.Name ?? string.Empty).ToLowerInvariant().Trim();

                // 1. Приоритетные ключевые слова для входной группы
                if (name.Contains("главный вход") || name.Contains("main entrance")) score += 2000;
                else if (name.Contains("вход персонала") || name.Contains("служебный вход")) score += 1200;
                else if (name.Contains("вход") || name.Contains("entrance")) score += 1500;
                else if (name.Contains("тамбур") || name.Contains("vestibule")) score += 1400;
                else if (name.Contains("вестибюль") || name.Contains("фойе") || name.Contains("foyer")) score += 1300;
                else if (name.Contains("прихожая")) score += 1100;
                else if (name.Contains("лобби") || name.Contains("lobby")) score += 1000;
                else if (name.Contains("холл") || name.Contains("hall")) score += 900;
                else if (name.Contains("ресепшн") || name.Contains("рецепция") || name.Contains("приемная") || name.Contains("приёмная")) score += 800;
                else if (name.Contains("гардероб")) score += 600;
                else if (name.Contains("коридор") || name.Contains("зал")) score += 300;

                // 2. Бонус за наличие наружной двери
                if (exteriorDoorRoomIds.Contains(room.Id))
                {
                    score += 1000;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRoom = room;
                }
            }

            if (bestRoom != null && bestScore > 0)
            {
                return bestRoom;
            }

            // Если специальных названий нет, выбираем самое южное (минимальный Y) или верхнее левое
            return roomsOnLevel
                .OrderBy(r => GetRoomCenter(r).Y)
                .ThenBy(r => GetRoomCenter(r).X)
                .First();
        }

        /// <summary>
        /// Двухпроходное безопасное присвоение номеров помещениям:
        /// 1 проход — временные GUID-номера (исключает конфликты «Elements have duplicate Number values» в Revit).
        /// 2 проход — чистые последовательные номера 1, 2, 3...
        /// </summary>
        private static int ApplyTwoPassRenumbering(
            Document doc,
            List<Room> rooms,
            int startNumber,
            string prefix)
        {
            if (rooms.Count == 0) return 0;

            // Проход 1: временные уникальные имена
            for (int i = 0; i < rooms.Count; i++)
            {
                Room r = rooms[i];
                Parameter? numParam = r.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                if (numParam != null && !numParam.IsReadOnly)
                {
                    try
                    {
                        numParam.Set($"_TMP_{Guid.NewGuid():N}_{i + 1}");
                    }
                    catch { }
                }
            }

            doc.Regenerate();

            // Проход 2: окончательные последовательные номера
            int renumberedCount = 0;
            for (int i = 0; i < rooms.Count; i++)
            {
                Room r = rooms[i];
                Parameter? numParam = r.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                if (numParam != null && !numParam.IsReadOnly)
                {
                    string finalNumber = string.IsNullOrEmpty(prefix)
                        ? (startNumber + i).ToString()
                        : $"{prefix}{startNumber + i}";

                    try
                    {
                        numParam.Set(finalNumber);
                        renumberedCount++;
                    }
                    catch { }
                }
            }

            return renumberedCount;
        }

        /// <summary>
        /// Изолирует неразмещенные или незамкнутые помещения (Area <= 0 или Location == null),
        /// присваивая им префикс "X-...", чтобы они не занимали чистые целые числа 1, 2, 3...
        /// </summary>
        private static int IsolateUnplacedRooms(Document doc)
        {
            int count = 0;
            try
            {
                List<Room> unplacedRooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .OfType<Room>()
                    .Where(r => r.Area <= 0.001 || r.Location == null)
                    .ToList();

                int idx = 1;
                foreach (Room r in unplacedRooms)
                {
                    Parameter? numParam = r.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                    if (numParam != null && !numParam.IsReadOnly)
                    {
                        string curVal = numParam.AsString() ?? string.Empty;
                        if (!curVal.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                numParam.Set($"X-{idx++}");
                                count++;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            return count;
        }

        /// <summary>
        /// Определяет ID помещений, имеющих выход через наружную дверь.
        /// </summary>
        private static HashSet<ElementId> FindRoomsWithExteriorDoors(Document doc)
        {
            HashSet<ElementId> roomIds = [];
            try
            {
                List<FamilyInstance> doors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .ToList();

                foreach (FamilyInstance door in doors)
                {
                    Room? fromRoom = door.FromRoom;
                    Room? toRoom = door.ToRoom;

                    // Если одна сторона двери пуста, дверь ведет наружу
                    if (fromRoom == null && toRoom != null)
                    {
                        roomIds.Add(toRoom.Id);
                    }
                    else if (toRoom == null && fromRoom != null)
                    {
                        roomIds.Add(fromRoom.Id);
                    }
                }
            }
            catch { }

            return roomIds;
        }

        /// <summary>
        /// Вычисляет геометрический центр помещения.
        /// </summary>
        private static XYZ GetRoomCenter(Room room)
        {
            if (room.Location is LocationPoint lp)
            {
                return lp.Point;
            }

            BoundingBoxXYZ? box = room.get_BoundingBox(null);
            if (box != null)
            {
                return (box.Min + box.Max) * 0.5;
            }

            return XYZ.Zero;
        }
    }
}
