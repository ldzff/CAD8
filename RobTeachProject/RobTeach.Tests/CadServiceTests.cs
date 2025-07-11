using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobTeach.Services;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using System;
using System.IO;
using System.Linq;
using System.Windows.Media; // For PathGeometry, ArcSegment etc.
using System.Collections.Generic;

namespace RobTeach.Tests
{
    [TestClass]
    public class CadServiceTests
    {
        private const double Tolerance = 1e-5;
        private static string TestArtifactsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestArtifacts");

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            if (!Directory.Exists(TestArtifactsDir))
            {
                Directory.CreateDirectory(TestArtifactsDir);
            }
            CreateSampleDxfFiles();
        }

        private static void CreateSampleDxfFiles()
        {
            // Minimal DXF with a single line
            var dxfLine = new DxfFile();
            dxfLine.Entities.Add(new DxfLine(new DxfPoint(10, 20, 0), new DxfPoint(30, 40, 0)));
            dxfLine.Save(Path.Combine(TestArtifactsDir, "line.dxf"));

            // Minimal DXF with a single arc
            var dxfArc = new DxfFile();
            // Center (0,0), R=5, Start=0, End=90 (CCW)
            dxfArc.Entities.Add(new DxfArc(new DxfPoint(0, 0, 0), 5, 0, 90));
            dxfArc.Save(Path.Combine(TestArtifactsDir, "arc.dxf"));

            // Minimal DXF with a single circle
            var dxfCircle = new DxfFile();
            dxfCircle.Entities.Add(new DxfCircle(new DxfPoint(5, 5, 0), 10)); // Center (5,5), R=10
            dxfCircle.Save(Path.Combine(TestArtifactsDir, "circle.dxf"));

            // Invalid DXF file content
            File.WriteAllText(Path.Combine(TestArtifactsDir, "invalid.dxf"), "This is not a DXF file.");
        }

        [TestMethod]
        public void LoadDxf_ValidFile_LoadsSuccessfully()
        {
            // Arrange
            var cadService = new CadService();
            string filePath = Path.Combine(TestArtifactsDir, "line.dxf");

            // Act
            DxfFile dxf = cadService.LoadDxf(filePath);

            // Assert
            Assert.IsNotNull(dxf);
            Assert.IsTrue(dxf.Entities.Any());
        }

        [TestMethod]
        [ExpectedException(typeof(FileNotFoundException))]
        public void LoadDxf_NonExistentFile_ThrowsFileNotFoundException()
        {
            // Arrange
            var cadService = new CadService();
            string filePath = Path.Combine(TestArtifactsDir, "nonexistent.dxf");

            // Act
            cadService.LoadDxf(filePath);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void LoadDxf_NullFilePath_ThrowsArgumentNullException()
        {
            // Arrange
            var cadService = new CadService();

            // Act
            cadService.LoadDxf(null);
        }

        [TestMethod]
        [ExpectedException(typeof(Exception))] // DxfFile.Load throws general Exception for bad format
        public void LoadDxf_InvalidDxfFile_ThrowsException()
        {
            // Arrange
            var cadService = new CadService();
            string filePath = Path.Combine(TestArtifactsDir, "invalid.dxf");

            // Act
            cadService.LoadDxf(filePath);
        }

        [TestMethod]
        public void GetWpfShapesFromDxf_Line_ReturnsCorrectWpfLine()
        {
            // Arrange
            var cadService = new CadService();
            var dxfFile = new DxfFile();
            var dxfLine = new DxfLine(new DxfPoint(10, 20, 0), new DxfPoint(50, 60, 0));
            dxfFile.Entities.Add(dxfLine);

            // Act
            List<System.Windows.Shapes.Shape> wpfShapes = cadService.GetWpfShapesFromDxf(dxfFile)
                                                                    .Where(s => s != null)
                                                                    .Select(s => s!)
                                                                    .ToList();


            // Assert
            Assert.AreEqual(1, wpfShapes.Count);
            Assert.IsInstanceOfType(wpfShapes[0], typeof(System.Windows.Shapes.Line));
            var wpfLine = (System.Windows.Shapes.Line)wpfShapes[0];
            Assert.AreEqual(dxfLine.P1.X, wpfLine.X1, Tolerance);
            Assert.AreEqual(dxfLine.P1.Y, wpfLine.Y1, Tolerance);
            Assert.AreEqual(dxfLine.P2.X, wpfLine.X2, Tolerance);
            Assert.AreEqual(dxfLine.P2.Y, wpfLine.Y2, Tolerance);
        }

        [TestMethod]
        public void GetWpfShapesFromDxf_Arc_ReturnsCorrectWpfPathWithArcSegment()
        {
            // Arrange
            var cadService = new CadService();
            var dxfFile = new DxfFile();
            // Arc: Center (0,0), Radius 10, StartAngle 0, EndAngle 90
            var dxfArc = new DxfArc(new DxfPoint(0, 0, 0), 10, 0, 90);
            dxfFile.Entities.Add(dxfArc);

            // Act
             List<System.Windows.Shapes.Shape> wpfShapes = cadService.GetWpfShapesFromDxf(dxfFile)
                                                                    .Where(s => s != null)
                                                                    .Select(s => s!)
                                                                    .ToList();

            // Assert
            Assert.AreEqual(1, wpfShapes.Count);
            Assert.IsInstanceOfType(wpfShapes[0], typeof(System.Windows.Shapes.Path));
            var wpfPath = (System.Windows.Shapes.Path)wpfShapes[0];
            Assert.IsInstanceOfType(wpfPath.Data, typeof(PathGeometry));
            var pathGeometry = (PathGeometry)wpfPath.Data;
            Assert.AreEqual(1, pathGeometry.Figures.Count);
            var pathFigure = pathGeometry.Figures[0];
            Assert.AreEqual(1, pathFigure.Segments.Count);
            Assert.IsInstanceOfType(pathFigure.Segments[0], typeof(ArcSegment));
            var arcSegment = (ArcSegment)pathFigure.Segments[0];

            // Expected start point: (0 + 10*cos(0)) , (0 + 10*sin(0)) = (10,0)
            // Expected end point:   (0 + 10*cos(90)), (0 + 10*sin(90)) = (0,10)
            Assert.AreEqual(10, pathFigure.StartPoint.X, Tolerance, "Arc StartPoint X mismatch");
            Assert.AreEqual(0, pathFigure.StartPoint.Y, Tolerance, "Arc StartPoint Y mismatch");
            Assert.AreEqual(0, arcSegment.Point.X, Tolerance, "ArcSegment EndPoint X mismatch");
            Assert.AreEqual(10, arcSegment.Point.Y, Tolerance, "ArcSegment EndPoint Y mismatch");
            Assert.AreEqual(dxfArc.Radius, arcSegment.Size.Width, Tolerance, "ArcSegment Size.Width mismatch");
            Assert.AreEqual(dxfArc.Radius, arcSegment.Size.Height, Tolerance, "ArcSegment Size.Height mismatch");
            Assert.IsFalse(arcSegment.IsLargeArc, "Arc should not be large for 90 deg sweep."); // 90 deg sweep
            Assert.AreEqual(SweepDirection.Counterclockwise, arcSegment.SweepDirection);
        }

        [TestMethod]
        public void GetWpfShapesFromDxf_Circle_ReturnsCorrectWpfPathWithEllipseGeometry()
        {
            // Arrange
            var cadService = new CadService();
            var dxfFile = new DxfFile();
            var dxfCircle = new DxfCircle(new DxfPoint(5, 10, 0), 15); // Center (5,10), Radius 15
            dxfFile.Entities.Add(dxfCircle);

            // Act
            List<System.Windows.Shapes.Shape> wpfShapes = cadService.GetWpfShapesFromDxf(dxfFile)
                                                                    .Where(s => s != null)
                                                                    .Select(s => s!)
                                                                    .ToList();

            // Assert
            Assert.AreEqual(1, wpfShapes.Count);
            Assert.IsInstanceOfType(wpfShapes[0], typeof(System.Windows.Shapes.Path));
            var wpfPath = (System.Windows.Shapes.Path)wpfShapes[0];
            Assert.IsInstanceOfType(wpfPath.Data, typeof(EllipseGeometry));
            var ellipseGeometry = (EllipseGeometry)wpfPath.Data;

            Assert.AreEqual(dxfCircle.Center.X, ellipseGeometry.Center.X, Tolerance);
            Assert.AreEqual(dxfCircle.Center.Y, ellipseGeometry.Center.Y, Tolerance);
            Assert.AreEqual(dxfCircle.Radius, ellipseGeometry.RadiusX, Tolerance);
            Assert.AreEqual(dxfCircle.Radius, ellipseGeometry.RadiusY, Tolerance);
        }

        [TestMethod]
        public void GetWpfShapesFromDxf_NullDxfFile_ReturnsEmptyList()
        {
            // Arrange
            var cadService = new CadService();

            // Act
            List<System.Windows.Shapes.Shape> wpfShapes = cadService.GetWpfShapesFromDxf(null)
                                                                    .Where(s => s != null)
                                                                    .Select(s => s!)
                                                                    .ToList();
            // Assert
            Assert.AreEqual(0, wpfShapes.Count);
        }

        [TestMethod]
        public void GetWpfShapesFromDxf_EmptyDxfFile_ReturnsEmptyList()
        {
            // Arrange
            var cadService = new CadService();
            var dxfFile = new DxfFile(); // No entities

            // Act
            List<System.Windows.Shapes.Shape> wpfShapes = cadService.GetWpfShapesFromDxf(dxfFile)
                                                                    .Where(s => s != null)
                                                                    .Select(s => s!)
                                                                    .ToList();
            // Assert
            Assert.AreEqual(0, wpfShapes.Count);
        }
    }
}
