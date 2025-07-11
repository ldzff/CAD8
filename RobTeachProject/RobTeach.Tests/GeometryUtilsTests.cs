using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobTeach.Utils;
using IxMilia.Dxf;
using System;

namespace RobTeach.Tests
{
    [TestClass]
    public class GeometryUtilsTests
    {
        private const double Tolerance = 1e-5; // Tolerance for floating point comparisons

        #region CalculateArcParametersFromThreePoints Tests

        [TestMethod]
        public void CalculateArcParametersFromThreePoints_SimpleArc_CalculatesCorrectly()
        {
            // Arrange: Arc with P1=(1,0,0), P2=(0,1,0), P3=(-1,0,0) -> Center=(0,0,0), Radius=1, CCW
            var p1 = new DxfPoint(1, 0, 0);
            var p2 = new DxfPoint(0, 1, 0);
            var p3 = new DxfPoint(-1, 0, 0);

            // Act
            var result = GeometryUtils.CalculateArcParametersFromThreePoints(p1, p2, p3);

            // Assert
            Assert.IsTrue(result.HasValue, "Result should have a value.");
            var (center, radius, startAngle, endAngle, normal, isClockwise) = result.Value;

            Assert.AreEqual(0, center.X, Tolerance, "Center X should be 0.");
            Assert.AreEqual(0, center.Y, Tolerance, "Center Y should be 0.");
            Assert.AreEqual(0, center.Z, Tolerance, "Center Z should be 0 (same as p1.Z).");
            Assert.AreEqual(1, radius, Tolerance, "Radius should be 1.");
            Assert.AreEqual(0, startAngle, Tolerance, "StartAngle should be 0 degrees.");
            Assert.AreEqual(180, endAngle, Tolerance, "EndAngle should be 180 degrees.");
            Assert.AreEqual(0, normal.X, Tolerance, "Normal X should be 0.");
            Assert.AreEqual(0, normal.Y, Tolerance, "Normal Y should be 0.");
            Assert.AreEqual(1, Math.Abs(normal.Z), Tolerance, "Normal Z magnitude should be 1."); // Could be +1 or -1 depending on point order interpretation, check isClockwise
            Assert.IsFalse(isClockwise, "Arc should be Counter-Clockwise.");
             // For this specific P1, P2, P3 order, CCW is expected, so normal.Z should be positive.
            Assert.IsTrue(normal.Z > 0, "Normal Z should be positive for CCW arc on XY plane from P1->P2->P3.");
        }

        [TestMethod]
        public void CalculateArcParametersFromThreePoints_CollinearPoints_ReturnsNull()
        {
            // Arrange
            var p1 = new DxfPoint(0, 0, 0);
            var p2 = new DxfPoint(1, 1, 0);
            var p3 = new DxfPoint(2, 2, 0);

            // Act
            var result = GeometryUtils.CalculateArcParametersFromThreePoints(p1, p2, p3);

            // Assert
            Assert.IsFalse(result.HasValue, "Result should be null for collinear points.");
        }

        [TestMethod]
        public void CalculateArcParametersFromThreePoints_CoincidentPoints_ReturnsNull()
        {
            // Arrange
            var p1 = new DxfPoint(1, 1, 0);
            var p2 = new DxfPoint(1, 1, 0);
            var p3 = new DxfPoint(2, 2, 0); // p1 and p2 are coincident

            // Act
            var result = GeometryUtils.CalculateArcParametersFromThreePoints(p1, p2, p3);

            // Assert
            Assert.IsFalse(result.HasValue, "Result should be null if any two points are coincident.");

            var p4 = new DxfPoint(1,1,0);
            var p5 = new DxfPoint(2,2,0);
            var p6 = new DxfPoint(2,2,0); // p2 and p3 coincident
            result = GeometryUtils.CalculateArcParametersFromThreePoints(p4, p5, p6);
            Assert.IsFalse(result.HasValue, "Result should be null if any two points are coincident (case 2).");
        }

        [TestMethod]
        public void CalculateArcParametersFromThreePoints_ClockwiseArc_CalculatesCorrectly()
        {
            // Arrange: Arc with P1=(-1,0,0), P2=(0,1,0), P3=(1,0,0) -> Center=(0,0,0), Radius=1, CW from P1->P2->P3
            // GeometryUtils should return angles for CCW (DxfArc standard)
            // So, P1=(-1,0,0) -> StartAngle=180. P3=(1,0,0) -> EndAngle=0. But it should be Start=0, End=180 if we reorder for CCW.
            // The `isClockwise` flag should indicate the P1->P2->P3 order was CW.
            // The returned startAngle and endAngle should represent the CCW path.
            var p1 = new DxfPoint(-1, 0, 0); // Original Start
            var p2 = new DxfPoint(0, 1, 0);  // Mid
            var p3 = new DxfPoint(1, 0, 0);   // Original End

            // Act
            var result = GeometryUtils.CalculateArcParametersFromThreePoints(p1, p2, p3);

            // Assert
            Assert.IsTrue(result.HasValue, "Result should have a value.");
            var (center, radius, startAngle, endAngle, normal, isClockwise) = result.Value;

            Assert.AreEqual(0, center.X, Tolerance, "Center X should be 0.");
            Assert.AreEqual(0, center.Y, Tolerance, "Center Y should be 0.");
            Assert.AreEqual(1, radius, Tolerance, "Radius should be 1.");
            Assert.IsTrue(isClockwise, "Arc P1->P2->P3 should be identified as Clockwise.");

            // For a CW input P1=(-1,0,0) P2=(0,1,0) P3=(1,0,0), the function should return
            // StartAngle = 0 (from P3) and EndAngle = 180 (from P1) to represent the CCW path.
            Assert.AreEqual(0, startAngle, Tolerance, "Effective StartAngle for CCW DxfArc should be 0 (from original P3).");
            Assert.AreEqual(180, endAngle, Tolerance, "Effective EndAngle for CCW DxfArc should be 180 (from original P1).");
            Assert.AreEqual(0, normal.X, Tolerance);
            Assert.AreEqual(0, normal.Y, Tolerance);
            Assert.AreEqual(1, Math.Abs(normal.Z), Tolerance); // Normal should point along Z for XY plane arc
            Assert.IsTrue(normal.Z > 0, "Normal Z should be positive for this XY plane arc."); // For this specific P1,P2,P3, normal is +Z
        }

        #endregion

        #region CalculateCircleCenterRadiusFromThreePoints Tests

        [TestMethod]
        public void CalculateCircleCenterRadiusFromThreePoints_XYPlane_CalculatesCorrectly()
        {
            // Arrange: Points forming a circle on XY plane: (1,0,0), (0,1,0), (-1,0,0)
            // Expected: Center (0,0,0), Radius 1, Normal (0,0,1) or (0,0,-1)
            var p1 = new DxfPoint(1, 0, 0);
            var p2 = new DxfPoint(0, 1, 0);
            var p3 = new DxfPoint(-1, 0, 0);

            // Act
            var result = GeometryUtils.CalculateCircleCenterRadiusFromThreePoints(p1, p2, p3);

            // Assert
            Assert.IsTrue(result.HasValue, "Result should have a value.");
            var (center, radius, normal) = result.Value;

            Assert.AreEqual(0, center.X, Tolerance, "Center X should be 0.");
            Assert.AreEqual(0, center.Y, Tolerance, "Center Y should be 0.");
            Assert.AreEqual(0, center.Z, Tolerance, "Center Z should be 0.");
            Assert.AreEqual(1, radius, Tolerance, "Radius should be 1.");
            Assert.AreEqual(0, normal.X, Tolerance, "Normal X should be 0.");
            Assert.AreEqual(0, normal.Y, Tolerance, "Normal Y should be 0.");
            Assert.AreEqual(1, Math.Abs(normal.Z), Tolerance, "Normal Z magnitude should be 1.");
            // Depending on cross product order (p2-p1)x(p3-p1), normal.Z could be +1 or -1.
            // For p1=(1,0,0), p2=(0,1,0), p3=(-1,0,0):
            // v12 = (-1,1,0), v13 = (-2,0,0)
            // v12 x v13 = (0,0,2). Normalized normal.Z should be 1.
            Assert.IsTrue(normal.Z > 0, "Normal Z should be positive.");
        }

        [TestMethod]
        public void CalculateCircleCenterRadiusFromThreePoints_XZPlane_CalculatesCorrectly()
        {
            // Arrange: Points forming a circle on XZ plane: (1,0,0), (0,0,1), (-1,0,0)
            // Expected: Center (0,0,0), Radius 1, Normal (0,1,0) or (0,-1,0)
            var p1 = new DxfPoint(1, 0, 0);
            var p2 = new DxfPoint(0, 0, 1);
            var p3 = new DxfPoint(-1, 0, 0);

            // Act
            var result = GeometryUtils.CalculateCircleCenterRadiusFromThreePoints(p1, p2, p3);

            // Assert
            Assert.IsTrue(result.HasValue, "Result should have a value.");
            var (center, radius, normal) = result.Value;

            Assert.AreEqual(0, center.X, Tolerance, "Center X should be 0.");
            Assert.AreEqual(0, center.Y, Tolerance, "Center Y should be 0.");
            Assert.AreEqual(0, center.Z, Tolerance, "Center Z should be 0.");
            Assert.AreEqual(1, radius, Tolerance, "Radius should be 1.");
            Assert.AreEqual(0, normal.X, Tolerance, "Normal X should be 0.");
            Assert.AreEqual(1, Math.Abs(normal.Y), Tolerance, "Normal Y magnitude should be 1.");
            Assert.AreEqual(0, normal.Z, Tolerance, "Normal Z should be 0.");
             // v12 = (-1,0,1), v13 = (-2,0,0)
            // v12 x v13 = (0,-(-2),0) = (0,2,0). Normalized normal.Y should be 1.
            Assert.IsTrue(normal.Y > 0, "Normal Y should be positive.");
        }


        [TestMethod]
        public void CalculateCircleCenterRadiusFromThreePoints_CollinearPoints_ReturnsNull()
        {
            // Arrange
            var p1 = new DxfPoint(0, 0, 0);
            var p2 = new DxfPoint(1, 1, 1);
            var p3 = new DxfPoint(2, 2, 2);

            // Act
            var result = GeometryUtils.CalculateCircleCenterRadiusFromThreePoints(p1, p2, p3);

            // Assert
            Assert.IsFalse(result.HasValue, "Result should be null for collinear points.");
        }

        [TestMethod]
        public void CalculateCircleCenterRadiusFromThreePoints_CoincidentPoints_ReturnsNull()
        {
            // Arrange
            var p1 = new DxfPoint(1, 0, 0);
            var p2 = new DxfPoint(1, 0, 0); // Coincident with p1
            var p3 = new DxfPoint(0, 1, 0);

            // Act
            var result = GeometryUtils.CalculateCircleCenterRadiusFromThreePoints(p1, p2, p3);

            // Assert
            Assert.IsFalse(result.HasValue, "Result should be null for coincident points.");
        }

        [TestMethod]
        public void CalculateCircleCenterRadiusFromThreePoints_LargeCoordinates_CalculatesCorrectly()
        {
            // Arrange: Shifted version of XY plane circle
            var offset = new DxfPoint(1000, 2000, 3000);
            var p1 = new DxfPoint(1 + offset.X, 0 + offset.Y, 0 + offset.Z);
            var p2 = new DxfPoint(0 + offset.X, 1 + offset.Y, 0 + offset.Z);
            var p3 = new DxfPoint(-1 + offset.X, 0 + offset.Y, 0 + offset.Z);
            // Expected: Center (offset.X, offset.Y, offset.Z), Radius 1, Normal (0,0,1) or (0,0,-1)

            // Act
            var result = GeometryUtils.CalculateCircleCenterRadiusFromThreePoints(p1, p2, p3);

            // Assert
            Assert.IsTrue(result.HasValue, "Result should have a value for large coordinates.");
            var (center, radius, normal) = result.Value;

            Assert.AreEqual(offset.X, center.X, Tolerance, "Center X should match offset.");
            Assert.AreEqual(offset.Y, center.Y, Tolerance, "Center Y should match offset.");
            Assert.AreEqual(offset.Z, center.Z, Tolerance, "Center Z should match offset."); // Z is from p1, p2, p3
            Assert.AreEqual(1, radius, Tolerance, "Radius should be 1.");
            Assert.AreEqual(0, normal.X, Tolerance);
            Assert.AreEqual(0, normal.Y, Tolerance);
            Assert.AreEqual(1, Math.Abs(normal.Z), Tolerance);
            Assert.IsTrue(normal.Z > 0, "Normal Z should be positive.");
        }

        #endregion
    }
}
