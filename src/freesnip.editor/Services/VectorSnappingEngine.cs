#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;

namespace freesnip.editor.Services
{
    public readonly struct ShapeSnapTarget
    {
        public readonly Rect Bounds;
        public readonly bool IsImageMiddle;

        public ShapeSnapTarget(Rect bounds, bool isImageMiddle = false)
        {
            Bounds = bounds;
            IsImageMiddle = isImageMiddle;
        }
    }

    public readonly struct AxisSnapResult
    {
        public readonly double Coordinate;
        public readonly bool IsMiddle;

        public AxisSnapResult(double coordinate, bool isMiddle)
        {
            Coordinate = coordinate;
            IsMiddle = isMiddle;
        }
    }

    public readonly struct SnapPair
    {
        public readonly Point Recipient;
        public readonly Point Held;
        public readonly double Distance;

        public SnapPair(Point recipient, Point held, double distance)
        {
            Recipient = recipient;
            Held = held;
            Distance = distance;
        }
    }

    public static class VectorSnappingEngine
    {
        public const double VectorSnapThreshold = 6.0;
        public const double VectorSnapGap = 12.0;
        public const double NearbyTargetThreshold = 8.0;
        public const double SoftSnapAngleTolerance = 0.04;

        public static Point SnapToEightDirectionsStrict(Point proposed, Point anchor)
        {
            if (!double.IsFinite(proposed.X) || !double.IsFinite(proposed.Y) ||
                !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y))
            {
                return proposed;
            }

            double dx = proposed.X - anchor.X;
            double dy = proposed.Y - anchor.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1.0) return proposed;

            double angle = Math.Atan2(dy, dx);
            double snappedAngle = Math.Round(angle / (Math.PI / 4.0)) * (Math.PI / 4.0);
            return new Point(anchor.X + Math.Cos(snappedAngle) * length, anchor.Y + Math.Sin(snappedAngle) * length);
        }

        public static Point SoftSnapToEightDirections(Point proposed, Point anchor)
        {
            if (!double.IsFinite(proposed.X) || !double.IsFinite(proposed.Y) ||
                !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y))
            {
                return proposed;
            }

            double dx = proposed.X - anchor.X;
            double dy = proposed.Y - anchor.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 10.0) return proposed;

            double angle = Math.Atan2(dy, dx);
            double snappedAngle = Math.Round(angle / (Math.PI / 4.0)) * (Math.PI / 4.0);

            if (Math.Abs(angle - snappedAngle) < SoftSnapAngleTolerance)
            {
                return new Point(anchor.X + Math.Cos(snappedAngle) * length, anchor.Y + Math.Sin(snappedAngle) * length);
            }

            return proposed;
        }

        public static Point PullBackFromTarget(Point anchor, Point target, double gap = VectorSnapGap)
        {
            if (!double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y) ||
                !double.IsFinite(target.X) || !double.IsFinite(target.Y) ||
                !double.IsFinite(gap))
            {
                return target;
            }

            double dx = target.X - anchor.X;
            double dy = target.Y - anchor.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= gap || length == 0) return target;
            return new Point(target.X - (dx / length) * gap, target.Y - (dy / length) * gap);
        }

        public static Point ApplyVectorConstraints(
            Point proposed,
            Point anchor,
            KeyModifiers modifiers,
            bool magneticSnappingEnabled,
            bool allowTargetSnap,
            IReadOnlyList<Point>? targets)
        {
            if (!magneticSnappingEnabled) return proposed;

            if (modifiers.HasFlag(KeyModifiers.Shift))
            {
                return SnapToEightDirectionsStrict(proposed, anchor);
            }

            if (modifiers.HasFlag(KeyModifiers.Alt)) return proposed;

            if (allowTargetSnap && targets != null && targets.Count > 0)
            {
                Point? bestTarget = null;
                double bestDistance = NearbyTargetThreshold;

                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    double dx = proposed.X - target.X;
                    double dy = proposed.Y - target.Y;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        bestTarget = target;
                    }
                }

                if (bestTarget.HasValue)
                {
                    return PullBackFromTarget(anchor, bestTarget.Value, VectorSnapGap);
                }
            }

            return SoftSnapToEightDirections(proposed, anchor);
        }

        public static bool TrySnapResizeEdge(
            double coordinate,
            IReadOnlyList<ShapeSnapTarget> targets,
            bool vertical,
            out double snapped,
            double threshold = VectorSnapThreshold)
        {
            snapped = coordinate;
            if (!double.IsFinite(coordinate) || targets == null || targets.Count == 0 || !double.IsFinite(threshold))
            {
                return false;
            }

            double bestDistance = threshold + 0.001;
            bool found = false;

            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                double a = vertical ? target.Bounds.Top : target.Bounds.Left;
                double b = vertical ? target.Bounds.Center.Y : target.Bounds.Center.X;
                double c = vertical ? target.Bounds.Bottom : target.Bounds.Right;

                double dA = Math.Abs(coordinate - a);
                if (dA <= threshold && dA < bestDistance)
                {
                    bestDistance = dA;
                    snapped = a;
                    found = true;
                }

                double dB = Math.Abs(coordinate - b);
                if (dB <= threshold && dB < bestDistance)
                {
                    bestDistance = dB;
                    snapped = b;
                    found = true;
                }

                double dC = Math.Abs(coordinate - c);
                if (dC <= threshold && dC < bestDistance)
                {
                    bestDistance = dC;
                    snapped = c;
                    found = true;
                }
            }

            return found;
        }

        public static bool TrySnapAxis(
            bool vertical,
            Rect moving,
            IReadOnlyList<ShapeSnapTarget> targets,
            out double delta,
            out AxisSnapResult snapped,
            double threshold = VectorSnapThreshold)
        {
            delta = 0;
            snapped = default;
            if (!double.IsFinite(moving.Left) || !double.IsFinite(moving.Top) ||
                !double.IsFinite(moving.Width) || !double.IsFinite(moving.Height) ||
                targets == null || targets.Count == 0 || !double.IsFinite(threshold))
            {
                return false;
            }

            double m0 = vertical ? moving.Top : moving.Left;
            double m1 = vertical ? moving.Center.Y : moving.Center.X;
            double m2 = vertical ? moving.Bottom : moving.Right;

            double bestDistance = threshold + 0.001;
            double bestDelta = 0;
            double bestCoordinate = 0;
            bool bestIsMiddle = false;
            bool found = false;

            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                double t0 = vertical ? target.Bounds.Top : target.Bounds.Left;
                double t1 = vertical ? target.Bounds.Center.Y : target.Bounds.Center.X;
                double t2 = vertical ? target.Bounds.Bottom : target.Bounds.Right;

                double targetMiddle = vertical
                    ? target.Bounds.Top + target.Bounds.Height / 2.0
                    : target.Bounds.Left + target.Bounds.Width / 2.0;

                TestAxisPoints(m0, t0, t1, t2, target.IsImageMiddle, targetMiddle, threshold, ref bestDistance, ref bestDelta, ref bestCoordinate, ref bestIsMiddle, ref found);
                TestAxisPoints(m1, t0, t1, t2, target.IsImageMiddle, targetMiddle, threshold, ref bestDistance, ref bestDelta, ref bestCoordinate, ref bestIsMiddle, ref found);
                TestAxisPoints(m2, t0, t1, t2, target.IsImageMiddle, targetMiddle, threshold, ref bestDistance, ref bestDelta, ref bestCoordinate, ref bestIsMiddle, ref found);
            }

            if (!found) return false;

            delta = bestDelta;
            snapped = new AxisSnapResult(bestCoordinate, bestIsMiddle);
            return true;
        }

        private static void TestAxisPoints(
            double m,
            double t0,
            double t1,
            double t2,
            bool isImageMiddle,
            double targetMiddle,
            double threshold,
            ref double bestDistance,
            ref double bestDelta,
            ref double bestCoordinate,
            ref bool bestIsMiddle,
            ref bool found)
        {
            TestSinglePoint(m, t0, isImageMiddle, targetMiddle, threshold, ref bestDistance, ref bestDelta, ref bestCoordinate, ref bestIsMiddle, ref found);
            TestSinglePoint(m, t1, isImageMiddle, targetMiddle, threshold, ref bestDistance, ref bestDelta, ref bestCoordinate, ref bestIsMiddle, ref found);
            TestSinglePoint(m, t2, isImageMiddle, targetMiddle, threshold, ref bestDistance, ref bestDelta, ref bestCoordinate, ref bestIsMiddle, ref found);
        }

        private static void TestSinglePoint(
            double m,
            double t,
            bool isImageMiddle,
            double targetMiddle,
            double threshold,
            ref double bestDistance,
            ref double bestDelta,
            ref double bestCoordinate,
            ref bool bestIsMiddle,
            ref bool found)
        {
            double distance = Math.Abs(m - t);
            if (distance <= threshold && distance < bestDistance)
            {
                bestDistance = distance;
                bestDelta = t - m;
                bestCoordinate = t;
                bestIsMiddle = isImageMiddle && Math.Abs(t - targetMiddle) < 0.5;
                found = true;
            }
        }

        public static void GeneratePerimeterPoints(
            Rect bounds,
            bool isRound,
            double activeCenterX,
            double activeCenterY,
            Action<double, double> addPoint)
        {
            double clampX = Math.Clamp(activeCenterX, bounds.Left, bounds.Right);
            double clampY = Math.Clamp(activeCenterY, bounds.Top, bounds.Bottom);

            double dTop = Math.Sqrt((activeCenterX - clampX) * (activeCenterX - clampX) + (activeCenterY - bounds.Top) * (activeCenterY - bounds.Top));
            double dBottom = Math.Sqrt((activeCenterX - clampX) * (activeCenterX - clampX) + (activeCenterY - bounds.Bottom) * (activeCenterY - bounds.Bottom));
            double dLeft = Math.Sqrt((activeCenterX - bounds.Left) * (activeCenterX - bounds.Left) + (activeCenterY - clampY) * (activeCenterY - clampY));
            double dRight = Math.Sqrt((activeCenterX - bounds.Right) * (activeCenterX - bounds.Right) + (activeCenterY - clampY) * (activeCenterY - clampY));

            double minD = Math.Min(Math.Min(dTop, dBottom), Math.Min(dLeft, dRight));

            if (isRound)
            {
                double cx = bounds.Left + bounds.Width / 2.0;
                double cy = bounds.Top + bounds.Height / 2.0;
                double rx = bounds.Width / 2.0;
                double ry = bounds.Height / 2.0;
                const double cos45 = 0.7071067811865475;
                const double sin45 = 0.7071067811865475;

                if (minD == dTop)
                {
                    addPoint(cx - rx * cos45, cy - ry * sin45);
                    addPoint(cx, bounds.Top);
                    addPoint(cx + rx * cos45, cy - ry * sin45);
                }
                else if (minD == dBottom)
                {
                    addPoint(cx - rx * cos45, cy + ry * sin45);
                    addPoint(cx, bounds.Bottom);
                    addPoint(cx + rx * cos45, cy + ry * sin45);
                }
                else if (minD == dLeft)
                {
                    addPoint(cx - rx * cos45, cy - ry * sin45);
                    addPoint(bounds.Left, cy);
                    addPoint(cx - rx * cos45, cy + ry * sin45);
                }
                else
                {
                    addPoint(cx + rx * cos45, cy - ry * sin45);
                    addPoint(bounds.Right, cy);
                    addPoint(cx + rx * cos45, cy + ry * sin45);
                }
            }
            else
            {
                if (minD == dTop)
                {
                    addPoint(bounds.Left, bounds.Top);
                    addPoint(bounds.Left + bounds.Width / 2.0, bounds.Top);
                    addPoint(bounds.Right, bounds.Top);
                }
                else if (minD == dBottom)
                {
                    addPoint(bounds.Left, bounds.Bottom);
                    addPoint(bounds.Left + bounds.Width / 2.0, bounds.Bottom);
                    addPoint(bounds.Right, bounds.Bottom);
                }
                else if (minD == dLeft)
                {
                    addPoint(bounds.Left, bounds.Top);
                    addPoint(bounds.Left, bounds.Top + bounds.Height / 2.0);
                    addPoint(bounds.Left, bounds.Bottom);
                }
                else
                {
                    addPoint(bounds.Right, bounds.Top);
                    addPoint(bounds.Right, bounds.Top + bounds.Height / 2.0);
                    addPoint(bounds.Right, bounds.Bottom);
                }
            }
        }

        public static void GenerateFacingPerimeterPoints(
            Rect heldBounds,
            bool isHeldRound,
            Rect recipientBounds,
            bool isRecipientRound,
            Action<double, double> addRecipientPoint,
            Action<double, double> addHeldPoint)
        {
            if (!double.IsFinite(heldBounds.Left) || !double.IsFinite(heldBounds.Top) ||
                !double.IsFinite(heldBounds.Width) || !double.IsFinite(heldBounds.Height) ||
                !double.IsFinite(recipientBounds.Left) || !double.IsFinite(recipientBounds.Top) ||
                !double.IsFinite(recipientBounds.Width) || !double.IsFinite(recipientBounds.Height) ||
                addRecipientPoint == null || addHeldPoint == null)
            {
                return;
            }

            double heldCenterX = heldBounds.Left + heldBounds.Width / 2.0;
            double heldCenterY = heldBounds.Top + heldBounds.Height / 2.0;

            double recipientCenterX = recipientBounds.Left + recipientBounds.Width / 2.0;
            double recipientCenterY = recipientBounds.Top + recipientBounds.Height / 2.0;

            GeneratePerimeterPoints(recipientBounds, isRecipientRound, heldCenterX, heldCenterY, addRecipientPoint);
            GeneratePerimeterPoints(heldBounds, isHeldRound, recipientCenterX, recipientCenterY, addHeldPoint);
        }

        public static bool TryFindMatchingSnapPair(
            IReadOnlyList<Point>? recipientPoints,
            IReadOnlyList<Point>? heldPoints,
            out SnapPair result,
            double threshold = VectorSnapThreshold)
        {
            result = default;
            if (recipientPoints == null || recipientPoints.Count == 0 ||
                heldPoints == null || heldPoints.Count == 0 ||
                !double.IsFinite(threshold))
            {
                return false;
            }

            double minDirectDist = threshold + 0.001;
            Point? bestHeld = null;
            Point? bestRecipient = null;

            for (int i = 0; i < heldPoints.Count; i++)
            {
                var h = heldPoints[i];
                if (!double.IsFinite(h.X) || !double.IsFinite(h.Y)) continue;

                for (int j = 0; j < recipientPoints.Count; j++)
                {
                    var r = recipientPoints[j];
                    if (!double.IsFinite(r.X) || !double.IsFinite(r.Y)) continue;

                    double dx = h.X - r.X;
                    double dy = h.Y - r.Y;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d <= threshold && d < minDirectDist)
                    {
                        minDirectDist = d;
                        bestHeld = h;
                        bestRecipient = r;
                    }
                }
            }

            if (bestHeld.HasValue && bestRecipient.HasValue)
            {
                result = new SnapPair(bestRecipient.Value, bestHeld.Value, minDirectDist);
                return true;
            }

            double minAxisDist = double.MaxValue;
            Point? bestAxisHeld = null;
            Point? bestAxisRecipient = null;

            for (int i = 0; i < heldPoints.Count; i++)
            {
                var h = heldPoints[i];
                if (!double.IsFinite(h.X) || !double.IsFinite(h.Y)) continue;

                for (int j = 0; j < recipientPoints.Count; j++)
                {
                    var r = recipientPoints[j];
                    if (!double.IsFinite(r.X) || !double.IsFinite(r.Y)) continue;

                    bool alignsX = Math.Abs(h.X - r.X) < 1.0;
                    bool alignsY = Math.Abs(h.Y - r.Y) < 1.0;
                    if (alignsX || alignsY)
                    {
                        double dx = h.X - r.X;
                        double dy = h.Y - r.Y;
                        double d = Math.Sqrt(dx * dx + dy * dy);
                        if (d < minAxisDist)
                        {
                            minAxisDist = d;
                            bestAxisHeld = h;
                            bestAxisRecipient = r;
                        }
                    }
                }
            }

            if (bestAxisHeld.HasValue && bestAxisRecipient.HasValue)
            {
                result = new SnapPair(bestAxisRecipient.Value, bestAxisHeld.Value, minAxisDist);
                return true;
            }

            return false;
        }

        public static void GetCircleKeypoints(Rect bounds, IList<Point> destination)
        {
            if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
                !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
                destination == null)
            {
                return;
            }

            double cx = bounds.Left + bounds.Width / 2.0;
            double cy = bounds.Top + bounds.Height / 2.0;
            double rx = bounds.Width / 2.0;
            double ry = bounds.Height / 2.0;
            const double cos45 = 0.7071067811865475;
            const double sin45 = 0.7071067811865475;

            destination.Add(new Point(cx, bounds.Top));
            destination.Add(new Point(bounds.Right, cy));
            destination.Add(new Point(cx, bounds.Bottom));
            destination.Add(new Point(bounds.Left, cy));
            destination.Add(new Point(cx + rx * cos45, cy - ry * sin45));
            destination.Add(new Point(cx + rx * cos45, cy + ry * sin45));
            destination.Add(new Point(cx - rx * cos45, cy + ry * sin45));
            destination.Add(new Point(cx - rx * cos45, cy - ry * sin45));
            destination.Add(new Point(cx, cy));
        }

        public static bool TrySnapKeypointsToTargets(
            IReadOnlyList<Point>? movingPoints,
            IReadOnlyList<Point>? targetPoints,
            out double snapDx,
            out double snapDy,
            out Point snappedMovingPoint,
            out Point snappedTargetPoint,
            double threshold = VectorSnapThreshold)
        {
            snapDx = 0;
            snapDy = 0;
            snappedMovingPoint = default;
            snappedTargetPoint = default;

            if (movingPoints == null || movingPoints.Count == 0 ||
                targetPoints == null || targetPoints.Count == 0 ||
                !double.IsFinite(threshold))
            {
                return false;
            }

            double bestDistance = threshold + 0.001;
            bool found = false;

            for (int i = 0; i < movingPoints.Count; i++)
            {
                var mp = movingPoints[i];
                if (!double.IsFinite(mp.X) || !double.IsFinite(mp.Y)) continue;

                for (int j = 0; j < targetPoints.Count; j++)
                {
                    var tp = targetPoints[j];
                    if (!double.IsFinite(tp.X) || !double.IsFinite(tp.Y)) continue;

                    double dx = tp.X - mp.X;
                    double dy = tp.Y - mp.Y;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d <= threshold && d < bestDistance)
                    {
                        bestDistance = d;
                        snapDx = dx;
                        snapDy = dy;
                        snappedMovingPoint = mp;
                        snappedTargetPoint = tp;
                        found = true;
                    }
                }
            }

            return found;
        }

        public static bool TrySnapKeypointSingleAxis(
            bool vertical,
            IReadOnlyList<Point>? keypoints,
            IReadOnlyList<ShapeSnapTarget>? targets,
            out double delta,
            out AxisSnapResult snapped,
            double threshold = VectorSnapThreshold)
        {
            delta = 0;
            snapped = default;
            if (keypoints == null || keypoints.Count == 0 ||
                targets == null || targets.Count == 0 ||
                !double.IsFinite(threshold))
            {
                return false;
            }

            double bestDistance = threshold + 0.001;
            double bestDelta = 0;
            double bestCoordinate = 0;
            bool bestIsMiddle = false;
            bool found = false;

            for (int k = 0; k < keypoints.Count; k++)
            {
                var kp = keypoints[k];
                double m = vertical ? kp.Y : kp.X;
                if (!double.IsFinite(m)) continue;

                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    double t0 = vertical ? target.Bounds.Top : target.Bounds.Left;
                    double t1 = vertical ? target.Bounds.Center.Y : target.Bounds.Center.X;
                    double t2 = vertical ? target.Bounds.Bottom : target.Bounds.Right;

                    double targetMiddle = vertical
                        ? target.Bounds.Top + target.Bounds.Height / 2.0
                        : target.Bounds.Left + target.Bounds.Width / 2.0;

                    TestSinglePoint(m, t0, target.IsImageMiddle, targetMiddle, threshold, ref bestDistance, ref bestDelta, ref bestCoordinate, ref bestIsMiddle, ref found);
                    TestSinglePoint(m, t1, target.IsImageMiddle, targetMiddle, threshold, ref bestDistance, ref bestDelta, ref bestCoordinate, ref bestIsMiddle, ref found);
                    TestSinglePoint(m, t2, target.IsImageMiddle, targetMiddle, threshold, ref bestDistance, ref bestDelta, ref bestCoordinate, ref bestIsMiddle, ref found);
                }
            }

            if (!found) return false;

            delta = bestDelta;
            snapped = new AxisSnapResult(bestCoordinate, bestIsMiddle);
            return true;
        }
    }
}
