using RobTeach.Models;
using System.Windows; // For Point
using System; // For Math
using System.Collections.Generic; // For List
using IxMilia.Dxf; // For DxfPoint, DxfVector
using System.Diagnostics; // For Debug.WriteLine


namespace RobTeach.Utils
{
    /// <summary>
    /// Provides utility methods related to trajectory calculations and manipulations.
    /// </summary>
    public static class TrajectoryUtils
    {
        /// <summary>
        /// Generates a list of 2D display points for a given trajectory.
        /// The points are stored in the trajectory's `Points` property.
        /// This method handles Line, Arc, and Circle primitive types.
        /// For Arcs and Circles, it discretizes them into line segments.
        /// </summary>
        /// <param name="trajectory">The trajectory to generate points for. Its `Points` list will be cleared and repopulated.</param>
        /// <param name="arcResolutionDegrees">The angular resolution in degrees for discretizing arcs and circles. Default is 15.0 degrees.</param>
        public static void GenerateDisplayPoints(Trajectory trajectory, double arcResolutionDegrees = 15.0)
        {
            if (trajectory == null)
            {
                AppLogger.Log("GenerateDisplayPoints: Trajectory is null.", LogLevel.Warning);
                return;
            }

            trajectory.Points.Clear();
            AppLogger.Log($"GenerateDisplayPoints: Generating points for trajectory type '{trajectory.PrimitiveType}', Reversed: {trajectory.IsReversed}.", LogLevel.Debug);

            switch (trajectory.PrimitiveType)
            {
                case "Line":
                    if (trajectory.IsReversed)
                    {
                        trajectory.Points.Add(new Point(trajectory.LineEndPoint.X, trajectory.LineEndPoint.Y));
                        trajectory.Points.Add(new Point(trajectory.LineStartPoint.X, trajectory.LineStartPoint.Y));
                    }
                    else
                    {
                        trajectory.Points.Add(new Point(trajectory.LineStartPoint.X, trajectory.LineStartPoint.Y));
                        trajectory.Points.Add(new Point(trajectory.LineEndPoint.X, trajectory.LineEndPoint.Y));
                    }
                    AppLogger.Log($"Generated {trajectory.Points.Count} points for Line.", LogLevel.Debug);
                    break;

                case "Arc":
                    if (trajectory.ArcPoint1 == null || trajectory.ArcPoint2 == null || trajectory.ArcPoint3 == null)
                    {
                        AppLogger.Log("GenerateDisplayPoints: Arc trajectory missing ArcPoint1/2/3 data.", LogLevel.Warning);
                        // Fallback: if OriginalDxfEntity is an arc, try to use its parameters. This is less ideal as ArcPoint1/2/3 should be primary.
                        if (trajectory.OriginalDxfEntity is DxfArc originalArc)
                        {
                            AppLogger.Log("GenerateDisplayPoints: Attempting fallback to OriginalDxfEntity (DxfArc) for point generation.", LogLevel.Debug);
                            var pointsFromOriginal = GenerateArcPointsFromDxfArc(originalArc, trajectory.IsReversed, arcResolutionDegrees);
                            trajectory.Points.AddRange(pointsFromOriginal);
                            AppLogger.Log($"Generated {trajectory.Points.Count} points for Arc from OriginalDxfEntity.", LogLevel.Debug);
                        }
                        return;
                    }

                    var arcParams = GeometryUtils.CalculateArcParametersFromThreePoints(
                        trajectory.ArcPoint1.Coordinates,
                        trajectory.ArcPoint2.Coordinates,
                        trajectory.ArcPoint3.Coordinates);

                    if (arcParams.HasValue)
                    {
                        var (center, radius, startAngleDeg, endAngleDeg, normal, isP1P2P3Clockwise) = arcParams.Value;

                        // Determine sweep direction based on IsReversed and natural P1-P2-P3 direction
                        // The startAngleDeg and endAngleDeg from GeometryUtils are for a CCW sweep for the DxfArc.
                        // If P1-P2-P3 was CW, then effectively (P3, P2, P1) is CCW.
                        // The 'isP1P2P3Clockwise' tells us the original user intent for P1->P2->P3.
                        // 'trajectory.IsReversed' tells us if the user wants to reverse THAT intent.

                        bool effectivelySweepClockwise;
                        double effectiveStartAngle, effectiveEndAngle;

                        if (trajectory.IsReversed) // User wants to reverse the P1->P2->P3 direction
                        {
                            effectivelySweepClockwise = !isP1P2P3Clockwise;
                            effectiveStartAngle = endAngleDeg; // Start from original P3's angle (which is endAngleDeg of CCW DxfArc)
                            effectiveEndAngle = startAngleDeg; // End at original P1's angle (startAngleDeg of CCW DxfArc)
                        }
                        else // User wants to follow P1->P2->P3 direction
                        {
                            effectivelySweepClockwise = isP1P2P3Clockwise;
                            effectiveStartAngle = startAngleDeg;
                            effectiveEndAngle = endAngleDeg;
                        }

                        // Adjust angles for sweep direction for the loop
                        if (effectivelySweepClockwise)
                        {
                            if (effectiveEndAngle > effectiveStartAngle) effectiveStartAngle += 360; // Ensure CW sweep
                        }
                        else // CCW
                        {
                            if (effectiveEndAngle < effectiveStartAngle) effectiveEndAngle += 360; // Ensure CCW sweep
                        }

                        double currentAngleDeg = effectiveStartAngle;
                        double step = effectivelySweepClockwise ? -arcResolutionDegrees : arcResolutionDegrees;
                        if (Math.Abs(step) < 1e-6) step = effectivelySweepClockwise ? -1.0 : 1.0;


                        List<Point> arcPoints = new List<Point>();
                        if (effectivelySweepClockwise)
                        {
                            while (currentAngleDeg >= effectiveEndAngle - Math.Abs(step) / 2.0)
                            {
                                double radAngle = currentAngleDeg * Math.PI / 180.0;
                                arcPoints.Add(new Point(center.X + radius * Math.Cos(radAngle), center.Y + radius * Math.Sin(radAngle)));
                                if (Math.Abs(currentAngleDeg - effectiveEndAngle) < 1e-5) break;
                                currentAngleDeg += step;
                                if (currentAngleDeg < effectiveEndAngle && currentAngleDeg > effectiveEndAngle + step - 1e-5 ) currentAngleDeg = effectiveEndAngle;
                            }
                        }
                        else // CCW
                        {
                            while (currentAngleDeg <= effectiveEndAngle + Math.Abs(step) / 2.0)
                            {
                                double radAngle = currentAngleDeg * Math.PI / 180.0;
                                arcPoints.Add(new Point(center.X + radius * Math.Cos(radAngle), center.Y + radius * Math.Sin(radAngle)));
                                if (Math.Abs(currentAngleDeg - effectiveEndAngle) < 1e-5) break;
                                currentAngleDeg += step;
                                if (currentAngleDeg > effectiveEndAngle && currentAngleDeg < effectiveEndAngle + step - 1e-5) currentAngleDeg = effectiveEndAngle;
                            }
                        }
                        trajectory.Points.AddRange(arcPoints);
                        AppLogger.Log($"Generated {trajectory.Points.Count} points for Arc from 3-point definition. EffectiveSweepCW: {effectivelySweepClockwise}, StartAngle: {effectiveStartAngle:F2}, EndAngle: {effectiveEndAngle:F2}", LogLevel.Debug);

                    }
                    else
                    {
                        AppLogger.Log("GenerateDisplayPoints: Could not calculate arc parameters for 3-point Arc. Defaulting to line segments if P1,P2,P3 exist.", LogLevel.Warning);
                        if (trajectory.ArcPoint1 != null) trajectory.Points.Add(new Point(trajectory.ArcPoint1.Coordinates.X, trajectory.ArcPoint1.Coordinates.Y));
                        if (trajectory.ArcPoint2 != null) trajectory.Points.Add(new Point(trajectory.ArcPoint2.Coordinates.X, trajectory.ArcPoint2.Coordinates.Y));
                        if (trajectory.ArcPoint3 != null) trajectory.Points.Add(new Point(trajectory.ArcPoint3.Coordinates.X, trajectory.ArcPoint3.Coordinates.Y));
                    }
                    break;

                case "Circle":
                    if (trajectory.CirclePoint1 == null || trajectory.CirclePoint2 == null || trajectory.CirclePoint3 == null)
                    {
                        AppLogger.Log("GenerateDisplayPoints: Circle trajectory missing CirclePoint1/2/3 data.", LogLevel.Warning);
                         // Fallback to OriginalDxfEntity if it's a circle
                        if (trajectory.OriginalDxfEntity is DxfCircle originalCircle)
                        {
                            AppLogger.Log("GenerateDisplayPoints: Attempting fallback to OriginalDxfEntity (DxfCircle) for point generation.", LogLevel.Debug);
                            var pointsFromOriginal = GenerateCirclePointsFromDxfCircle(originalCircle, arcResolutionDegrees);
                            trajectory.Points.AddRange(pointsFromOriginal);
                            AppLogger.Log($"Generated {trajectory.Points.Count} points for Circle from OriginalDxfEntity.", LogLevel.Debug);
                        }
                        return;
                    }

                    var circleParams = GeometryUtils.CalculateCircleCenterRadiusFromThreePoints(
                        trajectory.CirclePoint1.Coordinates,
                        trajectory.CirclePoint2.Coordinates,
                        trajectory.CirclePoint3.Coordinates);

                    if (circleParams.HasValue)
                    {
                        var (center, radius, normal) = circleParams.Value;
                        List<Point> circlePoints = new List<Point>();

                        // Determine local X and Y axes on the circle's plane
                        DxfVector localXAxis;
                        double arbThreshold = 1.0 / 64.0; // Standard DXF arbitrary axis algorithm threshold
                        if (Math.Abs(normal.X) < arbThreshold && Math.Abs(normal.Y) < arbThreshold)
                            localXAxis = (new DxfVector(0, 1, 0)).Cross(normal).Normalize(); // If normal is close to Z-axis, use Y-axis to cross
                        else
                            localXAxis = (DxfVector.ZAxis).Cross(normal).Normalize(); // Otherwise, use Z-axis
                        DxfVector localYAxis = normal.Cross(localXAxis).Normalize();

                        for (double angleDeg = 0; angleDeg < 360.0; angleDeg += arcResolutionDegrees)
                        {
                            double angleRad = angleDeg * Math.PI / 180.0;
                            DxfVector pointOnUnitCirclePlane = localXAxis * Math.Cos(angleRad) + localYAxis * Math.Sin(angleRad);
                            DxfPoint pointOnCircle = center + pointOnUnitCirclePlane * radius;
                            circlePoints.Add(new Point(pointOnCircle.X, pointOnCircle.Y)); // Project to XY for display
                        }
                        // Close the circle
                        if (circlePoints.Count > 0)
                        {
                             DxfVector firstPointOnUnitCirclePlane = localXAxis; // At angle 0
                             DxfPoint firstPointActual = center + firstPointOnUnitCirclePlane * radius;
                             circlePoints.Add(new Point(firstPointActual.X, firstPointActual.Y));
                        }
                        trajectory.Points.AddRange(circlePoints);
                        AppLogger.Log($"Generated {trajectory.Points.Count} points for Circle from 3-point definition.", LogLevel.Debug);
                    }
                    else
                    {
                        AppLogger.Log($"GenerateDisplayPoints: Could not calculate circle parameters for 3-point Circle. Fallback to 3 points if they exist.", LogLevel.Warning);
                        if (trajectory.CirclePoint1 != null) trajectory.Points.Add(new Point(trajectory.CirclePoint1.Coordinates.X, trajectory.CirclePoint1.Coordinates.Y));
                        if (trajectory.CirclePoint2 != null) trajectory.Points.Add(new Point(trajectory.CirclePoint2.Coordinates.X, trajectory.CirclePoint2.Coordinates.Y));
                        if (trajectory.CirclePoint3 != null) trajectory.Points.Add(new Point(trajectory.CirclePoint3.Coordinates.X, trajectory.CirclePoint3.Coordinates.Y));
                        if(trajectory.Points.Count > 1 && trajectory.CirclePoint1 != null) // Close if possible
                           trajectory.Points.Add(new Point(trajectory.CirclePoint1.Coordinates.X, trajectory.CirclePoint1.Coordinates.Y));
                    }
                    break;

                default:
                    AppLogger.Log($"GenerateDisplayPoints: Unknown or unsupported PrimitiveType '{trajectory.PrimitiveType}'. Points will be empty.", LogLevel.Warning);
                    // Fallback to original DxfEntity if available and a known type
                    if (trajectory.OriginalDxfEntity != null)
                    {
                        AppLogger.Log($"GenerateDisplayPoints: Attempting fallback to OriginalDxfEntity type '{trajectory.OriginalDxfEntity.GetType().Name}'.", LogLevel.Debug);
                        List<Point> fallbackPoints = new List<Point>();
                        switch (trajectory.OriginalDxfEntity)
                        {
                            case DxfLine line:
                                fallbackPoints.AddRange(GenerateLinePointsFromDxfLine(line, trajectory.IsReversed));
                                break;
                            case DxfArc arc:
                                fallbackPoints.AddRange(GenerateArcPointsFromDxfArc(arc, trajectory.IsReversed, arcResolutionDegrees));
                                break;
                            case DxfCircle circle:
                                fallbackPoints.AddRange(GenerateCirclePointsFromDxfCircle(circle, arcResolutionDegrees));
                                break;
                            // Add other DxfEntity types if needed
                        }
                        trajectory.Points.AddRange(fallbackPoints);
                        AppLogger.Log($"Generated {trajectory.Points.Count} points for Trajectory from OriginalDxfEntity fallback.", LogLevel.Debug);
                    }
                    break;
            }
        }

        // Helper for line from DxfLine (used in fallback)
        /// <summary>
        /// Generates two points (start and end) for a DxfLine.
        /// Considers the IsReversed flag.
        /// </summary>
        private static List<Point> GenerateLinePointsFromDxfLine(DxfLine line, bool isReversed)
        {
            var points = new List<Point>();
            if (line == null) return points;
            if (isReversed)
            {
                points.Add(new Point(line.P2.X, line.P2.Y));
                points.Add(new Point(line.P1.X, line.P1.Y));
            }
            else
            {
                points.Add(new Point(line.P1.X, line.P1.Y));
                points.Add(new Point(line.P2.X, line.P2.Y));
            }
            return points;
        }

        // Helper for arc from DxfArc (used in fallback)
        /// <summary>
        /// Discretizes a DxfArc into a list of points based on angular resolution.
        /// Considers the IsReversed flag to sweep the arc in the opposite direction.
        /// </summary>
        private static List<Point> GenerateArcPointsFromDxfArc(DxfArc arc, bool isReversed, double resolutionDegrees)
        {
            var points = new List<Point>();
            if (arc == null || resolutionDegrees <= 0) return points;

            double startAngle = arc.StartAngle;
            double endAngle = arc.EndAngle;

            if (isReversed)
            {
                double temp = startAngle;
                startAngle = endAngle;
                endAngle = temp;
            }

            if (endAngle < startAngle) endAngle += 360.0; // Ensure CCW sweep for loop

            double currentAngle = startAngle;
            while(currentAngle <= endAngle + (resolutionDegrees/2.0)) // Ensure end point is included
            {
                if (currentAngle > endAngle) currentAngle = endAngle; // Cap at end angle

                double radAngle = currentAngle * Math.PI / 180.0;
                double x = arc.Center.X + arc.Radius * Math.Cos(radAngle);
                double y = arc.Center.Y + arc.Radius * Math.Sin(radAngle);
                points.Add(new Point(x, y));

                if (Math.Abs(currentAngle - endAngle) < 1e-5) break; // Reached end
                currentAngle += resolutionDegrees;
            }
            return points;
        }

        // Helper for circle from DxfCircle (used in fallback)
        /// <summary>
        /// Discretizes a DxfCircle into a list of points based on angular resolution.
        /// The points form a closed polygon approximating the circle.
        /// </summary>
        private static List<Point> GenerateCirclePointsFromDxfCircle(DxfCircle circle, double resolutionDegrees)
        {
            var points = new List<Point>();
            if (circle == null || resolutionDegrees <= 0) return points;

            for (double angleDeg = 0; angleDeg < 360.0; angleDeg += resolutionDegrees)
            {
                double angleRad = angleDeg * Math.PI / 180.0;
                // Assuming circle is on XY plane or its normal defines the plane.
                // For simplicity, using XY projection as display points are 2D.
                // More complex: use circle.Normal to define plane and project.
                double x = circle.Center.X + circle.Radius * Math.Cos(angleRad);
                double y = circle.Center.Y + circle.Radius * Math.Sin(angleRad);
                points.Add(new Point(x, y));
            }
            // Close the circle
            if (points.Count > 0)
            {
                 double firstPointX = circle.Center.X + circle.Radius * Math.Cos(0);
                 double firstPointY = circle.Center.Y + circle.Radius * Math.Sin(0);
                 points.Add(new Point(firstPointX, firstPointY));
            }
            return points;
        }


        /// <summary>
        /// Calculates the total length of a trajectory based on its discretized 2D points.
        /// Assumes points are in millimeters and returns the length in meters.
        /// </summary>
        /// <param name="trajectory">The trajectory whose length is to be calculated.</param>
        /// <returns>The total length of the trajectory in meters.</returns>
        public static double CalculateTrajectoryLength(Trajectory trajectory)
        {
            if (trajectory == null || trajectory.Points == null || trajectory.Points.Count < 2)
            {
                return 0.0;
            }

            double length = 0.0;
            for (int i = 0; i < trajectory.Points.Count - 1; i++)
            {
                Point p1 = trajectory.Points[i];
                Point p2 = trajectory.Points[i + 1];
                length += Point.Subtract(p2, p1).Length;
            }
            return length / 1000.0; // Assuming points are in mm, convert to meters
        }

        /// <summary>
        /// Calculates the minimum runtime for a trajectory based on its length and a predefined maximum speed.
        /// Assumes a maximum speed of 2 m/s.
        /// </summary>
        /// <param name="trajectory">The trajectory for which to calculate the minimum runtime.</param>
        /// <returns>The minimum runtime in seconds.</returns>
        public static double CalculateMinRuntime(Trajectory trajectory)
        {
            if (trajectory == null) return 0.0;
            double lengthInMeters = CalculateTrajectoryLength(trajectory);
            if (lengthInMeters <= 0) return 0.0; // Avoid division by zero or negative runtime for zero-length trajectories
            // Speed = 2 m/s
            // If length is very small, e.g., less than a precision threshold, consider runtime effectively zero or a very small number.
            // For now, direct calculation:
            if (Math.Abs(lengthInMeters) < 1e-9) return 0.0; // Treat extremely small lengths as zero length for runtime calculation
            return lengthInMeters / 2.0;
        }
    }
}
