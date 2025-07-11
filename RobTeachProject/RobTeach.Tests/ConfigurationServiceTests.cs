using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobTeach.Models;
using RobTeach.Services; // For ConfigurationService and converters
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using System;
using System.Text.Json;
using System.Collections.Generic; // For List
using System.IO; // For Path and File operations

namespace RobTeach.Tests
{
    [TestClass]
    public class ConfigurationServiceTests
    {
        private const double Tolerance = 1e-5; // For DxfPoint/Vector comparisons in entities

        private static JsonSerializerOptions GetJsonSerializerOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = {
                    new DxfPointJsonConverter(),
                    new DxfVectorJsonConverter(),
                    new TrajectoryJsonConverter()
                }
            };
        }

        private bool AreDxfPointsEqual(DxfPoint p1, DxfPoint p2, double tol)
        {
            return Math.Abs(p1.X - p2.X) < tol &&
                   Math.Abs(p1.Y - p2.Y) < tol &&
                   Math.Abs(p1.Z - p2.Z) < tol;
        }

        private bool AreDxfVectorsEqual(DxfVector v1, DxfVector v2, double tol)
        {
             return Math.Abs(v1.X - v2.X) < tol &&
                    Math.Abs(v1.Y - v2.Y) < tol &&
                    Math.Abs(v1.Z - v2.Z) < tol;
        }


        [TestMethod]
        public void Trajectory_SerializeDeserialize_Line_Correctly()
        {
            // Arrange
            var options = GetJsonSerializerOptions();
            var originalTrajectory = new Trajectory
            {
                OriginalEntityHandle = "HandleLine123",
                EntityType = "LINE",
                PrimitiveType = "Line",
                LineStartPoint = new DxfPoint(1, 2, 3),
                LineEndPoint = new DxfPoint(4, 5, 6),
                IsReversed = true,
                UpperNozzleGasOn = true,
                Runtime = 2.5
            };

            // Act
            string json = JsonSerializer.Serialize(originalTrajectory, options);
            var deserializedTrajectory = JsonSerializer.Deserialize<Trajectory>(json, options);

            // Assert
            Assert.IsNotNull(deserializedTrajectory);
            Assert.AreEqual(originalTrajectory.OriginalEntityHandle, deserializedTrajectory.OriginalEntityHandle);
            Assert.AreEqual(originalTrajectory.EntityType, deserializedTrajectory.EntityType);
            Assert.AreEqual(originalTrajectory.PrimitiveType, deserializedTrajectory.PrimitiveType);
            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.LineStartPoint, deserializedTrajectory.LineStartPoint, Tolerance));
            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.LineEndPoint, deserializedTrajectory.LineEndPoint, Tolerance));
            Assert.AreEqual(originalTrajectory.IsReversed, deserializedTrajectory.IsReversed);
            Assert.AreEqual(originalTrajectory.UpperNozzleGasOn, deserializedTrajectory.UpperNozzleGasOn);
            Assert.AreEqual(originalTrajectory.Runtime, deserializedTrajectory.Runtime, Tolerance);

            Assert.IsNotNull(deserializedTrajectory.OriginalDxfEntity);
            Assert.IsInstanceOfType(deserializedTrajectory.OriginalDxfEntity, typeof(DxfLine));
            var lineEntity = (DxfLine)deserializedTrajectory.OriginalDxfEntity;
            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.LineStartPoint, lineEntity.P1, Tolerance));
            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.LineEndPoint, lineEntity.P2, Tolerance));
        }

        [TestMethod]
        public void Trajectory_SerializeDeserialize_Arc_Correctly()
        {
            // Arrange
            var options = GetJsonSerializerOptions();
            var originalTrajectory = new Trajectory
            {
                OriginalEntityHandle = "HandleArc789",
                EntityType = "ARC",
                PrimitiveType = "Arc",
                ArcPoint1 = new TrajectoryPointWithAngles(new DxfPoint(1, 0, 0), 10, 0, 0),
                ArcPoint2 = new TrajectoryPointWithAngles(new DxfPoint(0, 1, 0), 0, 10, 0),
                ArcPoint3 = new TrajectoryPointWithAngles(new DxfPoint(-1, 0, 0), 0, 0, 10),
                LowerNozzleLiquidOn = true,
                Runtime = 5.0
            };

            // Act
            string json = JsonSerializer.Serialize(originalTrajectory, options);
            var deserializedTrajectory = JsonSerializer.Deserialize<Trajectory>(json, options);

            // Assert
            Assert.IsNotNull(deserializedTrajectory);
            Assert.AreEqual(originalTrajectory.PrimitiveType, deserializedTrajectory.PrimitiveType);
            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.ArcPoint1.Coordinates, deserializedTrajectory.ArcPoint1.Coordinates, Tolerance));
            Assert.AreEqual(originalTrajectory.ArcPoint1.Rx, deserializedTrajectory.ArcPoint1.Rx, Tolerance);
            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.ArcPoint2.Coordinates, deserializedTrajectory.ArcPoint2.Coordinates, Tolerance));
            Assert.AreEqual(originalTrajectory.ArcPoint2.Ry, deserializedTrajectory.ArcPoint2.Ry, Tolerance);
            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.ArcPoint3.Coordinates, deserializedTrajectory.ArcPoint3.Coordinates, Tolerance));
            Assert.AreEqual(originalTrajectory.ArcPoint3.Rz, deserializedTrajectory.ArcPoint3.Rz, Tolerance);
            Assert.AreEqual(originalTrajectory.LowerNozzleLiquidOn, deserializedTrajectory.LowerNozzleLiquidOn);
            Assert.AreEqual(originalTrajectory.Runtime, deserializedTrajectory.Runtime, Tolerance);

            Assert.IsNotNull(deserializedTrajectory.OriginalDxfEntity, "OriginalDxfEntity should be reconstructed for Arc.");
            Assert.IsInstanceOfType(deserializedTrajectory.OriginalDxfEntity, typeof(DxfArc));
            var arcEntity = (DxfArc)deserializedTrajectory.OriginalDxfEntity;

            // Verify reconstructed Arc geometry (center (0,0,0), radius 1 for these points)
            Assert.IsTrue(AreDxfPointsEqual(new DxfPoint(0,0,0), arcEntity.Center, Tolerance), "Reconstructed Arc Center mismatch");
            Assert.AreEqual(1.0, arcEntity.Radius, Tolerance, "Reconstructed Arc Radius mismatch");
            // StartAngle for P1(1,0,0) is 0. EndAngle for P3(-1,0,0) is 180.
            // The converter uses GeometryUtils which determines the CCW path.
            Assert.AreEqual(0, arcEntity.StartAngle, Tolerance, "Reconstructed Arc StartAngle mismatch");
            Assert.AreEqual(180, arcEntity.EndAngle, Tolerance, "Reconstructed Arc EndAngle mismatch");
        }

        [TestMethod]
        public void Trajectory_SerializeDeserialize_Circle_Correctly()
        {
            // Arrange
            var options = GetJsonSerializerOptions();
            var originalTrajectory = new Trajectory
            {
                OriginalEntityHandle = "HandleCircle456",
                EntityType = "CIRCLE",
                PrimitiveType = "Circle",
                CirclePoint1 = new TrajectoryPointWithAngles(new DxfPoint(10, 0, 5)), // Z=5
                CirclePoint2 = new TrajectoryPointWithAngles(new DxfPoint(0, 10, 5)),
                CirclePoint3 = new TrajectoryPointWithAngles(new DxfPoint(-10, 0, 5)),
                OriginalCircleCenter = new DxfPoint(0, 0, 5),
                OriginalCircleRadius = 10,
                OriginalCircleNormal = DxfVector.ZAxis,
                Runtime = 10.0
            };

            // Act
            string json = JsonSerializer.Serialize(originalTrajectory, options);
            var deserializedTrajectory = JsonSerializer.Deserialize<Trajectory>(json, options);

            // Assert
            Assert.IsNotNull(deserializedTrajectory);
            Assert.AreEqual(originalTrajectory.PrimitiveType, deserializedTrajectory.PrimitiveType);
            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.CirclePoint1.Coordinates, deserializedTrajectory.CirclePoint1.Coordinates, Tolerance));
            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.CirclePoint2.Coordinates, deserializedTrajectory.CirclePoint2.Coordinates, Tolerance));
            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.CirclePoint3.Coordinates, deserializedTrajectory.CirclePoint3.Coordinates, Tolerance));

            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.OriginalCircleCenter, deserializedTrajectory.OriginalCircleCenter, Tolerance));
            Assert.AreEqual(originalTrajectory.OriginalCircleRadius, deserializedTrajectory.OriginalCircleRadius, Tolerance);
            Assert.IsTrue(AreDxfVectorsEqual(originalTrajectory.OriginalCircleNormal, deserializedTrajectory.OriginalCircleNormal, Tolerance));
            Assert.AreEqual(originalTrajectory.Runtime, deserializedTrajectory.Runtime, Tolerance);

            Assert.IsNotNull(deserializedTrajectory.OriginalDxfEntity, "OriginalDxfEntity should be reconstructed for Circle.");
            Assert.IsInstanceOfType(deserializedTrajectory.OriginalDxfEntity, typeof(DxfCircle));
            var circleEntity = (DxfCircle)deserializedTrajectory.OriginalDxfEntity;
            Assert.IsTrue(AreDxfPointsEqual(originalTrajectory.OriginalCircleCenter, circleEntity.Center, Tolerance));
            Assert.AreEqual(originalTrajectory.OriginalCircleRadius, circleEntity.Radius, Tolerance);
            Assert.IsTrue(AreDxfVectorsEqual(originalTrajectory.OriginalCircleNormal, circleEntity.Normal, Tolerance));
        }

        [TestMethod]
        public void Configuration_SaveAndLoad_BasicProperties_Correctly()
        {
            // Arrange
            var configService = new ConfigurationService();
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"test_config_{Guid.NewGuid()}.json");

            var originalConfig = new Configuration
            {
                ProductName = "TestProduct1",
                DxfFileContent = "<dxf>sample content</dxf>",
                ModbusIpAddress = "192.168.1.100",
                ModbusPort = 503,
                CanvasState = new CanvasViewSettings { ScaleX = 1.5, TranslateY = 100 },
                CurrentPassIndex = 0
            };

            try
            {
                // Act
                configService.SaveConfiguration(originalConfig, tempFilePath);
                var loadedConfig = configService.LoadConfiguration(tempFilePath);

                // Assert
                Assert.IsNotNull(loadedConfig);
                Assert.AreEqual(originalConfig.ProductName, loadedConfig.ProductName);
                Assert.AreEqual(originalConfig.DxfFileContent, loadedConfig.DxfFileContent);
                Assert.AreEqual(originalConfig.ModbusIpAddress, loadedConfig.ModbusIpAddress);
                Assert.AreEqual(originalConfig.ModbusPort, loadedConfig.ModbusPort);
                Assert.AreEqual(originalConfig.CanvasState.ScaleX, loadedConfig.CanvasState.ScaleX, Tolerance);
                Assert.AreEqual(originalConfig.CanvasState.TranslateY, loadedConfig.CanvasState.TranslateY, Tolerance);
                Assert.AreEqual(originalConfig.CurrentPassIndex, loadedConfig.CurrentPassIndex);
            }
            finally
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
        }

        [TestMethod]
        public void Configuration_SaveAndLoad_WithSprayPassesAndTrajectories_Correctly()
        {
            // Arrange
            var configService = new ConfigurationService();
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"test_config_complex_{Guid.NewGuid()}.json");

            var lineTraj = new Trajectory
            {
                PrimitiveType = "Line",
                LineStartPoint = new DxfPoint(0,0,0), LineEndPoint = new DxfPoint(1,1,1),
                Runtime = 1.0
            };
            var arcTraj = new Trajectory
            {
                PrimitiveType = "Arc",
                ArcPoint1 = new TrajectoryPointWithAngles(new DxfPoint(1,0,0)),
                ArcPoint2 = new TrajectoryPointWithAngles(new DxfPoint(0,1,0)),
                ArcPoint3 = new TrajectoryPointWithAngles(new DxfPoint(-1,0,0)),
                Runtime = 2.0
            };

            var pass1 = new SprayPass { PassName = "Pass One", Trajectories = new List<Trajectory> { lineTraj } };
            var pass2 = new SprayPass { PassName = "Pass Two", Trajectories = new List<Trajectory> { arcTraj, lineTraj } };

            var originalConfig = new Configuration
            {
                ProductName = "ComplexProduct",
                SprayPasses = new List<SprayPass> { pass1, pass2 },
                CurrentPassIndex = 1,
                SelectedTrajectoryIndexInCurrentPass = 0
            };

            try
            {
                // Act
                configService.SaveConfiguration(originalConfig, tempFilePath);
                var loadedConfig = configService.LoadConfiguration(tempFilePath);

                // Assert
                Assert.IsNotNull(loadedConfig);
                Assert.AreEqual(originalConfig.ProductName, loadedConfig.ProductName);
                Assert.AreEqual(originalConfig.CurrentPassIndex, loadedConfig.CurrentPassIndex);
                Assert.AreEqual(originalConfig.SelectedTrajectoryIndexInCurrentPass, loadedConfig.SelectedTrajectoryIndexInCurrentPass);

                Assert.AreEqual(2, loadedConfig.SprayPasses.Count);
                Assert.AreEqual("Pass One", loadedConfig.SprayPasses[0].PassName);
                Assert.AreEqual(1, loadedConfig.SprayPasses[0].Trajectories.Count);
                Assert.AreEqual("Line", loadedConfig.SprayPasses[0].Trajectories[0].PrimitiveType);
                Assert.IsTrue(AreDxfPointsEqual(new DxfPoint(1,1,1), loadedConfig.SprayPasses[0].Trajectories[0].LineEndPoint, Tolerance));
                Assert.AreEqual(1.0, loadedConfig.SprayPasses[0].Trajectories[0].Runtime, Tolerance);


                Assert.AreEqual("Pass Two", loadedConfig.SprayPasses[1].PassName);
                Assert.AreEqual(2, loadedConfig.SprayPasses[1].Trajectories.Count);
                Assert.AreEqual("Arc", loadedConfig.SprayPasses[1].Trajectories[0].PrimitiveType);
                Assert.IsTrue(AreDxfPointsEqual(new DxfPoint(0,1,0), loadedConfig.SprayPasses[1].Trajectories[0].ArcPoint2.Coordinates, Tolerance));
                 Assert.AreEqual(2.0, loadedConfig.SprayPasses[1].Trajectories[0].Runtime, Tolerance);

                // Check OriginalDxfEntity reconstruction for the arc in the loaded config
                var loadedArcTraj = loadedConfig.SprayPasses[1].Trajectories[0];
                Assert.IsNotNull(loadedArcTraj.OriginalDxfEntity);
                Assert.IsInstanceOfType(loadedArcTraj.OriginalDxfEntity, typeof(DxfArc));
                var loadedArcEntity = (DxfArc)loadedArcTraj.OriginalDxfEntity;
                Assert.IsTrue(AreDxfPointsEqual(new DxfPoint(0,0,0), loadedArcEntity.Center, Tolerance));
                Assert.AreEqual(1.0, loadedArcEntity.Radius, Tolerance);

            }
            finally
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
        }

        [TestMethod]
        public void LoadConfiguration_NonExistentFile_ReturnsNull()
        {
            // Arrange
            var configService = new ConfigurationService();
            string nonExistentFilePath = Path.Combine(Path.GetTempPath(), $"non_existent_config_{Guid.NewGuid()}.json");

            // Act
            var loadedConfig = configService.LoadConfiguration(nonExistentFilePath);

            // Assert
            Assert.IsNull(loadedConfig);
        }

        [TestMethod]
        public void LoadConfiguration_InvalidJsonFile_ReturnsNull()
        {
            // Arrange
            var configService = new ConfigurationService();
            string invalidJsonFilePath = Path.Combine(Path.GetTempPath(), $"invalid_config_{Guid.NewGuid()}.json");
            File.WriteAllText(invalidJsonFilePath, "This is not valid JSON {");

            try
            {
                // Act
                var loadedConfig = configService.LoadConfiguration(invalidJsonFilePath);

                // Assert
                Assert.IsNull(loadedConfig);
            }
            finally
            {
                if (File.Exists(invalidJsonFilePath))
                {
                    File.Delete(invalidJsonFilePath);
                }
            }
        }
    }
}
