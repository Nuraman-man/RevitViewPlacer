using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitViewPlacer
{
    /// <summary>
    /// Автоматическое построение 4 ортогональных фасадов здания (Южный, Северный, Восточный, Западный / в осях по ГОСТ 21.501-93),
    /// если в модели они отсутствуют.
    /// </summary>
    public static class ElevationSheetBuilder
    {
        private const double MmToFeet = 1.0 / 304.8;

        public sealed record FacadeDefinition(
            string Code,
            string RussianName,
            string AxesGostTitle,
            XYZ MarkerPoint,
            XYZ TargetRightDir,
            double HalfWidthFt,
            double HeightAboveFt,
            double FarClipDepthFt);

        /// <summary>
        /// Проверяет, есть ли уже в модели фасад для заданной стороны света (SOUTH, NORTH, EAST, WEST)
        /// по имени (без учета регистра) и по вектору направления взгляда/правой стороны.
        /// </summary>
        public static bool IsElevationMatchingSide(
            View elevView,
            string sideCode,
            string numStart,
            string numEnd,
            string letStart,
            string letEnd)
        {
            string n = elevView.Name ?? string.Empty;

            if (sideCode == "SOUTH")
            {
                if (n.Contains("южн", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("south", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains($"{numStart}–{numEnd}", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains($"{numStart}-{numEnd}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (elevView is ViewSection vs && vs.RightDirection.X > 0.75) return true;
            }
            else if (sideCode == "NORTH")
            {
                if (n.Contains("север", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("north", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains($"{numEnd}–{numStart}", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains($"{numEnd}-{numStart}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (elevView is ViewSection vs && vs.RightDirection.X < -0.75) return true;
            }
            else if (sideCode == "EAST")
            {
                if (n.Contains("вост", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("east", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains($"{letStart}–{letEnd}", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains($"{letStart}-{letEnd}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (elevView is ViewSection vs && vs.RightDirection.Y > 0.75) return true;
            }
            else if (sideCode == "WEST")
            {
                if (n.Contains("запад", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("west", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains($"{letEnd}–{letStart}", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains($"{letEnd}-{letStart}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (elevView is ViewSection vs && vs.RightDirection.Y < -0.75) return true;
            }

            return false;
        }

        /// <summary>
        /// Проверяет наличие фасадов в проекте. Если каких-либо из 4 основных фасадов не хватает,
        /// автоматически строит недостающие фасады вокруг здания по ГОСТ 21.501-93.
        /// </summary>
        public static List<ViewSection> EnsureStandardElevationsExist(
            Document doc,
            BoundingBoxXYZ? buildingBox,
            bool createSouth = true,
            bool createNorth = true,
            bool createEast = true,
            bool createWest = true)
        {
            List<ViewSection> existingElevations = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection))
                .Cast<ViewSection>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Elevation)
                .ToList();

            if (buildingBox == null)
            {
                return existingElevations;
            }

            ViewFamilyType? elevationVft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Elevation);

            ViewFamilyType? sectionVft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Section);

            ViewPlan? hostPlan = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(vp => !vp.IsTemplate && vp.ViewType == ViewType.FloorPlan)
                .OrderBy(vp => vp.GenLevel?.Elevation ?? 0.0)
                .FirstOrDefault();

            if (elevationVft == null && sectionVft == null)
            {
                return existingElevations;
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

            (string numStart, string numEnd, string letStart, string letEnd) = SheetLayoutEngine.GetExtremeGridLabels(doc);

            // 4 направления фасадов по ГОСТ 21.501-93:
            // 1. Южный (в осях 1–N): наблюдатель на Юге (Y < bMinY), смотрит на Север (+Y), вправо на чертеже = +X (от оси 1 к N)
            // 2. Северный (в осях N–1): наблюдатель на Севере (Y > bMaxY), смотрит на Юг (-Y), вправо на чертеже = -X (от оси N к 1)
            // 3. Восточный (в осях А–Я): наблюдатель на Востоке (X > bMaxX), смотрит на Запад (-X), вправо на чертеже = +Y (от оси А к Я)
            // 4. Западный (в осях Я–А): наблюдатель на Западе (X < bMinX), смотрит на Восток (+X), вправо на чертеже = -Y (от оси Я к А)
            double offsetFrontFt = 4500.0 * MmToFeet;

            List<FacadeDefinition> defs =
            [
                new FacadeDefinition(
                    Code: "SOUTH",
                    RussianName: $"АР_Фасад в осях {numStart}–{numEnd}",
                    AxesGostTitle: $"Фасад в осях {numStart}–{numEnd}",
                    MarkerPoint: new XYZ(centerX, bMinY - offsetFrontFt, baseZ),
                    TargetRightDir: XYZ.BasisX,
                    HalfWidthFt: (widthX * 0.55) + (4000.0 * MmToFeet),
                    HeightAboveFt: heightZ + (2500.0 * MmToFeet),
                    FarClipDepthFt: lengthY + (10000.0 * MmToFeet)
                ),
                new FacadeDefinition(
                    Code: "NORTH",
                    RussianName: $"АР_Фасад в осях {numEnd}–{numStart}",
                    AxesGostTitle: $"Фасад в осях {numEnd}–{numStart}",
                    MarkerPoint: new XYZ(centerX, bMaxY + offsetFrontFt, baseZ),
                    TargetRightDir: -XYZ.BasisX,
                    HalfWidthFt: (widthX * 0.55) + (4000.0 * MmToFeet),
                    HeightAboveFt: heightZ + (2500.0 * MmToFeet),
                    FarClipDepthFt: lengthY + (10000.0 * MmToFeet)
                ),
                new FacadeDefinition(
                    Code: "EAST",
                    RussianName: $"АР_Фасад в осях {letStart}–{letEnd}",
                    AxesGostTitle: $"Фасад в осях {letStart}–{letEnd}",
                    MarkerPoint: new XYZ(bMaxX + offsetFrontFt, centerY, baseZ),
                    TargetRightDir: XYZ.BasisY,
                    HalfWidthFt: (lengthY * 0.55) + (4000.0 * MmToFeet),
                    HeightAboveFt: heightZ + (2500.0 * MmToFeet),
                    FarClipDepthFt: widthX + (10000.0 * MmToFeet)
                ),
                new FacadeDefinition(
                    Code: "WEST",
                    RussianName: $"АР_Фасад в осях {letEnd}–{letStart}",
                    AxesGostTitle: $"Фасад в осях {letEnd}–{letStart}",
                    MarkerPoint: new XYZ(bMinX - offsetFrontFt, centerY, baseZ),
                    TargetRightDir: -XYZ.BasisY,
                    HalfWidthFt: (lengthY * 0.55) + (4000.0 * MmToFeet),
                    HeightAboveFt: heightZ + (2500.0 * MmToFeet),
                    FarClipDepthFt: widthX + (10000.0 * MmToFeet)
                )
            ];

            foreach (FacadeDefinition fd in defs)
            {
                bool shouldCreate = fd.Code switch
                {
                    "SOUTH" => createSouth,
                    "NORTH" => createNorth,
                    "EAST" => createEast,
                    "WEST" => createWest,
                    _ => false
                };

                if (!shouldCreate) continue;

                bool alreadyExists = existingElevations.Any(ev =>
                    IsElevationMatchingSide(ev, fd.Code, numStart, numEnd, letStart, letEnd));

                if (alreadyExists) continue;

                try
                {
                    ViewSection? createdView = null;

                    if (elevationVft != null && hostPlan != null)
                    {
                        createdView = CreateTrueElevationView(doc, elevationVft.Id, hostPlan.Id, fd, bMinZ, bMaxZ);
                    }

                    if (createdView == null && sectionVft != null)
                    {
                        createdView = CreateFallbackSectionElevationView(
                            doc,
                            sectionVft.Id,
                            fd.MarkerPoint,
                            fd.TargetRightDir,
                            fd.HalfWidthFt,
                            depthBelowFt: 1500.0 * MmToFeet,
                            heightAboveFt: fd.HeightAboveFt,
                            farClipDepthFt: fd.FarClipDepthFt);
                    }

                    if (createdView != null)
                    {
                        AssignUniqueViewName(doc, createdView, fd.RussianName);
                        existingElevations.Add(createdView);
                    }
                }
                catch
                {
                    // Защита от локальных ограничений модели
                }
            }

            return existingElevations;
        }

        private static ViewSection? CreateTrueElevationView(
            Document doc,
            ElementId elevationVftId,
            ElementId hostPlanId,
            FacadeDefinition fd,
            double bMinZ,
            double bMaxZ)
        {
            ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, elevationVftId, fd.MarkerPoint, 100);
            ViewSection elevView = marker.CreateElevation(doc, hostPlanId, 0);

            XYZ curRight = elevView.RightDirection.Normalize();
            XYZ targetRight = fd.TargetRightDir.Normalize();

            double angle = Math.Atan2(targetRight.Y, targetRight.X) - Math.Atan2(curRight.Y, curRight.X);
            if (Math.Abs(angle) > 1e-4)
            {
                Line rotAxis = Line.CreateBound(fd.MarkerPoint, fd.MarkerPoint + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, marker.Id, rotAxis, angle);
                doc.Regenerate();
            }

            // Настраиваем глубину видимости и подрезку фасада
            try
            {
                Parameter? farClipActive = elevView.get_Parameter(BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR);
                if (farClipActive is { IsReadOnly: false })
                {
                    farClipActive.Set(1);
                }

                Parameter? farClipOffset = elevView.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                if (farClipOffset is { IsReadOnly: false })
                {
                    farClipOffset.Set(fd.FarClipDepthFt);
                }
            }
            catch { }

            try
            {
                BoundingBoxXYZ cb = elevView.CropBox;
                Transform t = cb.Transform;
                XYZ origin = t.Origin;

                double vMin = (bMinZ - (2000.0 * MmToFeet)) - origin.Z;
                double vMax = (bMaxZ + (3000.0 * MmToFeet)) - origin.Z;

                elevView.CropBox = new BoundingBoxXYZ
                {
                    Transform = t,
                    Min = new XYZ(-fd.HalfWidthFt, vMin, cb.Min.Z),
                    Max = new XYZ(fd.HalfWidthFt, vMax, cb.Max.Z)
                };
                elevView.CropBoxActive = true;
                elevView.CropBoxVisible = false;
            }
            catch { }

            return elevView;
        }

        private static ViewSection CreateFallbackSectionElevationView(
            Document doc,
            ElementId sectionVftId,
            XYZ centerPoint,
            XYZ alongDir,
            double halfWidthFt,
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
                Min = new XYZ(-halfWidthFt, -depthBelowFt, 0.0),
                Max = new XYZ(halfWidthFt, heightAboveFt, Math.Max(3000.0 * MmToFeet, farClipDepthFt))
            };

            return ViewSection.CreateSection(doc, sectionVftId, sectionBox);
        }

        private static void AssignUniqueViewName(Document doc, View view, string baseName)
        {
            HashSet<string> existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.Id != view.Id)
                .Select(v => v.Name ?? string.Empty)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string target = baseName;
            int counter = 2;
            while (existing.Contains(target))
            {
                target = $"{baseName} ({counter++})";
            }

            try
            {
                view.Name = target;
            }
            catch
            {
                try { view.Name = $"{target}_{Guid.NewGuid().ToString("N")[..4]}"; } catch { }
            }
        }
    }
}
