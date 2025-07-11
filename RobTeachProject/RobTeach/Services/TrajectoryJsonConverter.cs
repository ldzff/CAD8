using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using RobTeach.Models;
using IxMilia.Dxf; // For DxfPoint and DxfVector
using IxMilia.Dxf.Entities; // For DxfLine, DxfArc, DxfCircle etc.
using RobTeach.Utils; // Added for GeometryUtils

namespace RobTeach.Services
{
    /// <summary>
    /// Custom JSON converter for the <see cref="Trajectory"/> class.
    /// This converter handles the serialization and deserialization of Trajectory objects,
    /// including the reconstruction of the `OriginalDxfEntity` property based on
    /// the persisted geometric properties and primitive type.
    /// </summary>
    public class TrajectoryJsonConverter : JsonConverter<Trajectory>
    {
        /// <summary>
        /// Reads and converts the JSON to type <see cref="Trajectory"/>.
        /// It deserializes common properties and then, based on `PrimitiveType`,
        /// deserializes specific geometric properties (e.g., LineStartPoint/EndPoint, ArcPoint1/2/3).
        /// Crucially, it attempts to reconstruct the `OriginalDxfEntity` (e.g., DxfLine, DxfArc, DxfCircle)
        /// from these geometric properties so it can be used for reconciliation or display,
        /// even if the original DXF file isn't loaded.
        /// </summary>
        public override Trajectory Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected StartObject token");
            }

            using (JsonDocument jsonDocument = JsonDocument.ParseValue(ref reader))
            {
                JsonElement root = jsonDocument.RootElement;
                var trajectory = new Trajectory();

                // Helper function to get string property
                string GetStringProperty(JsonElement element, string propertyName)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
                           ? property.GetString() ?? string.Empty // Ensure null is converted to empty string if GetString() returns null
                           : string.Empty;
                }

                // Helper function for boolean property
                bool GetBooleanProperty(JsonElement element, string propertyName, bool defaultValue = false)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property) && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
                           ? property.GetBoolean()
                           : defaultValue;
                }

                // Helper function for int property
                int GetIntProperty(JsonElement element, string propertyName, int defaultValue = 0)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.Number
                           ? property.GetInt32()
                           : defaultValue;
                }

                // Helper function for double property
                double GetDoubleProperty(JsonElement element, string propertyName, double defaultValue = 0.0)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.Number
                           ? property.GetDouble()
                           : defaultValue;
                }

                // Helper function for DxfPoint
                DxfPoint GetDxfPointProperty(JsonElement element, string propertyName, JsonSerializerOptions options)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property)
                           ? JsonSerializer.Deserialize<DxfPoint>(property.GetRawText(), options)
                           : DxfPoint.Origin;
                }

                // Helper function for DxfVector
                DxfVector GetDxfVectorProperty(JsonElement element, string propertyName, JsonSerializerOptions options)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property)
                           ? JsonSerializer.Deserialize<DxfVector>(property.GetRawText(), options)
                           // Note: DxfVector is a struct, so ?? DxfVector.Zero might also be redundant if Deserialize never returns null for structs.
                           // However, for consistency with potential future changes or if it were a class, keeping it for DxfVector for now.
                           // Or, if DxfVectorJsonConverter handles missing properties by returning default, this ?? is also not strictly needed.
                           // For this change, only DxfPoint was specified.
                           // The above comment is now outdated as we are removing the ?? DxfVector.Zero as per instruction for DxfVector as well.
                           : DxfVector.Zero;
                }

                // Deserialize common properties
                trajectory.OriginalEntityHandle = GetStringProperty(root, "OriginalEntityHandle");
                trajectory.EntityType = GetStringProperty(root, "EntityType");
                trajectory.PrimitiveType = GetStringProperty(root, "PrimitiveType");

                trajectory.IsReversed = GetBooleanProperty(root, "IsReversed");
                trajectory.NozzleNumber = GetIntProperty(root, "NozzleNumber");

                trajectory.UpperNozzleEnabled = GetBooleanProperty(root, "UpperNozzleEnabled");
                trajectory.UpperNozzleGasOn = GetBooleanProperty(root, "UpperNozzleGasOn");
                trajectory.UpperNozzleLiquidOn = GetBooleanProperty(root, "UpperNozzleLiquidOn");
                trajectory.LowerNozzleEnabled = GetBooleanProperty(root, "LowerNozzleEnabled");
                trajectory.LowerNozzleGasOn = GetBooleanProperty(root, "LowerNozzleGasOn");
                trajectory.LowerNozzleLiquidOn = GetBooleanProperty(root, "LowerNozzleLiquidOn");

                // Runtime property
                trajectory.Runtime = GetDoubleProperty(root, "Runtime", 0.0); // Default to 0.0 if not present

                // Conditionally deserialize geometric properties
                if (trajectory.PrimitiveType == "Line")
                {
                    trajectory.LineStartPoint = GetDxfPointProperty(root, "LineStartPoint", options);
                    trajectory.LineEndPoint = GetDxfPointProperty(root, "LineEndPoint", options);
                }
                else if (trajectory.PrimitiveType == "Arc")
                {
                    // Properties like ArcCenter, ArcRadius etc. no longer exist on Trajectory object.
                    // These were removed in favor of ArcPoint1, ArcPoint2, ArcPoint3.
                    // Deserialization of these new properties will be handled when the JSON format is updated for them.
                    // For now, to fix compile error, we don't read these old properties onto trajectory.
                    // The OriginalDxfEntity for Arc will also not be created from these non-existent trajectory fields here.
                    // UPDATE: Deserialize ArcPoint1, ArcPoint2, ArcPoint3
                    if (root.TryGetProperty("ArcPoint1", out JsonElement arcPoint1Element))
                    {
                        trajectory.ArcPoint1 = JsonSerializer.Deserialize<TrajectoryPointWithAngles>(arcPoint1Element.GetRawText(), options) ?? new TrajectoryPointWithAngles();
                    }
                    if (root.TryGetProperty("ArcPoint2", out JsonElement arcPoint2Element))
                    {
                        trajectory.ArcPoint2 = JsonSerializer.Deserialize<TrajectoryPointWithAngles>(arcPoint2Element.GetRawText(), options) ?? new TrajectoryPointWithAngles();
                    }
                    if (root.TryGetProperty("ArcPoint3", out JsonElement arcPoint3Element))
                    {
                        trajectory.ArcPoint3 = JsonSerializer.Deserialize<TrajectoryPointWithAngles>(arcPoint3Element.GetRawText(), options) ?? new TrajectoryPointWithAngles();
                    }
                }
                else if (trajectory.PrimitiveType == "Circle")
                {
                    // trajectory.CircleCenter = GetDxfPointProperty(root, "CircleCenter", options); // Old property
                    // trajectory.CircleRadius = GetDoubleProperty(root, "CircleRadius"); // Old property
                    // trajectory.CircleNormal = GetDxfVectorProperty(root, "CircleNormal", options); // Old property
                    if (root.TryGetProperty("CirclePoint1", out JsonElement circlePoint1Element))
                    {
                        trajectory.CirclePoint1 = JsonSerializer.Deserialize<TrajectoryPointWithAngles>(circlePoint1Element.GetRawText(), options) ?? new TrajectoryPointWithAngles();
                    }
                    if (root.TryGetProperty("CirclePoint2", out JsonElement circlePoint2Element))
                    {
                        trajectory.CirclePoint2 = JsonSerializer.Deserialize<TrajectoryPointWithAngles>(circlePoint2Element.GetRawText(), options) ?? new TrajectoryPointWithAngles();
                    }
                    if (root.TryGetProperty("CirclePoint3", out JsonElement circlePoint3Element))
                    {
                        trajectory.CirclePoint3 = JsonSerializer.Deserialize<TrajectoryPointWithAngles>(circlePoint3Element.GetRawText(), options) ?? new TrajectoryPointWithAngles();
                    }
                    // Deserialize original circle parameters
                    trajectory.OriginalCircleCenter = GetDxfPointProperty(root, "OriginalCircleCenter", options);
                    trajectory.OriginalCircleRadius = GetDoubleProperty(root, "OriginalCircleRadius");
                    trajectory.OriginalCircleNormal = GetDxfVectorProperty(root, "OriginalCircleNormal", options);
                }

                // After all properties of Trajectory are deserialized,
                // create and assign OriginalDxfEntity based on these properties.
                // This allows the Trajectory object to have a DxfEntity representation even if loaded solely from JSON.
                // This is useful for geometric calculations or reconciliation with a subsequently loaded DXF file.
                switch (trajectory.PrimitiveType)
                {
                    case "Line":
                        trajectory.OriginalDxfEntity = new DxfLine(trajectory.LineStartPoint, trajectory.LineEndPoint);
                        break;
                    case "Arc":
                        // Attempt to reconstruct DxfArc from ArcPoint1, ArcPoint2, ArcPoint3 using GeometryUtils.
                        // This provides a DxfArc representation based on the 3-point definition.
                        if (trajectory.ArcPoint1 != null && trajectory.ArcPoint2 != null && trajectory.ArcPoint3 != null)
                        {
                            var arcParams = GeometryUtils.CalculateArcParametersFromThreePoints(
                                trajectory.ArcPoint1.Coordinates,
                                trajectory.ArcPoint2.Coordinates,
                                trajectory.ArcPoint3.Coordinates);

                            if (arcParams.HasValue)
                            {
                                trajectory.OriginalDxfEntity = new DxfArc(
                                    arcParams.Value.Center,
                                    arcParams.Value.Radius,
                                    arcParams.Value.StartAngle, // CCW start angle
                                    arcParams.Value.EndAngle)   // CCW end angle
                                {
                                    Normal = arcParams.Value.Normal
                                };
                            }
                            else
                            {
                                // If parameters can't be calculated (e.g., collinear points), OriginalDxfEntity remains null.
                                System.Diagnostics.Debug.WriteLine($"[TrajectoryJsonConverter] Read: Could not reconstruct DxfArc for trajectory {trajectory.OriginalEntityHandle} from 3 points.");
                            }
                        }
                        break;
                    case "Circle":
                        // Reconstruct DxfCircle using the deserialized OriginalCircleCenter, OriginalCircleRadius, OriginalCircleNormal.
                        // These are stored explicitly for Circles to ensure robust reconstruction matching the original DXF entity.
                        if (trajectory.OriginalCircleRadius > 0) // Basic validation
                        {
                            trajectory.OriginalDxfEntity = new DxfCircle(
                                trajectory.OriginalCircleCenter,
                                trajectory.OriginalCircleRadius)
                            {
                                Normal = trajectory.OriginalCircleNormal
                            };
                             System.Diagnostics.Debug.WriteLine($"[TrajectoryJsonConverter] Read: Reconstructed DxfCircle for trajectory {trajectory.OriginalEntityHandle} using Original parameters.");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[TrajectoryJsonConverter] Read: Could not reconstruct DxfCircle for trajectory {trajectory.OriginalEntityHandle} as OriginalCircleRadius is invalid or not set. OriginalDxfEntity will be null.");
                        }
                        break;
                    case "LwPolyline":
                        // Currently, the Trajectory model does not store detailed geometric data (vertices, bulges) for LwPolylines.
                        // Therefore, OriginalDxfEntity for LwPolylines cannot be reconstructed from Trajectory properties alone.
                        // It would be populated if this trajectory is later matched with a live DxfLwPolyline entity.
                        // A placeholder DxfLwPolyline could be created if EntityType indicates LwPolyline, but it would lack geometry.
                        if (trajectory.OriginalDxfEntity == null && trajectory.EntityType == typeof(DxfLwPolyline).Name) {
                             // trajectory.OriginalDxfEntity = new DxfLwPolyline(); // Example placeholder, but not geometrically useful.
                             System.Diagnostics.Debug.WriteLine($"[TrajectoryJsonConverter] Read: LwPolyline trajectory {trajectory.OriginalEntityHandle} - OriginalDxfEntity not reconstructed from JSON properties.");
                        }
                        break;
                }
                return trajectory;
            }
        }

        /// <summary>
        /// Writes a <see cref="Trajectory"/> object as JSON.
        /// Serializes common properties and specific geometric properties based on `PrimitiveType`.
        /// The `OriginalDxfEntity` itself is not serialized (it's marked `JsonIgnore` on the model).
        /// </summary>
        public override void Write(Utf8JsonWriter writer, Trajectory value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            // Common string properties
            if (!string.IsNullOrEmpty(value.OriginalEntityHandle))
            {
                writer.WriteString("OriginalEntityHandle", value.OriginalEntityHandle);
            }
            if (!string.IsNullOrEmpty(value.EntityType))
            {
                writer.WriteString("EntityType", value.EntityType);
            }
            if (!string.IsNullOrEmpty(value.PrimitiveType))
            {
                writer.WriteString("PrimitiveType", value.PrimitiveType);
            }

            // Boolean properties
            writer.WriteBoolean("IsReversed", value.IsReversed);
            writer.WriteNumber("NozzleNumber", value.NozzleNumber); // Assuming NozzleNumber is int as per typical usage

            // Nozzle control boolean properties
            writer.WriteBoolean("UpperNozzleEnabled", value.UpperNozzleEnabled);
            writer.WriteBoolean("UpperNozzleGasOn", value.UpperNozzleGasOn);
            writer.WriteBoolean("UpperNozzleLiquidOn", value.UpperNozzleLiquidOn);
            writer.WriteBoolean("LowerNozzleEnabled", value.LowerNozzleEnabled);
            writer.WriteBoolean("LowerNozzleGasOn", value.LowerNozzleGasOn);
            writer.WriteBoolean("LowerNozzleLiquidOn", value.LowerNozzleLiquidOn);

            // Runtime property
            writer.WriteNumber("Runtime", value.Runtime);

            // Geometric properties based on PrimitiveType
            switch (value.PrimitiveType)
            {
                case "Line":
                    writer.WritePropertyName("LineStartPoint");
                    JsonSerializer.Serialize(writer, value.LineStartPoint, options);
                    writer.WritePropertyName("LineEndPoint");
                    JsonSerializer.Serialize(writer, value.LineEndPoint, options);
                    break;
                case "Arc":
                    // Old Arc properties (ArcCenter, ArcRadius, etc.) have been removed from Trajectory model.
                    // New 3-point arc properties (ArcPoint1, ArcPoint2, ArcPoint3) will be serialized
                    // in a later stage when this converter is fully updated for the new model.
                    // For now, to fix compile errors, we write no specific geometric data for "Arc" type.
                    // This means Arc geometry won't be persisted correctly in this interim state.
                    // UPDATE: Serialize ArcPoint1, ArcPoint2, ArcPoint3
                    writer.WritePropertyName("ArcPoint1");
                    JsonSerializer.Serialize(writer, value.ArcPoint1, options);
                    writer.WritePropertyName("ArcPoint2");
                    JsonSerializer.Serialize(writer, value.ArcPoint2, options);
                    writer.WritePropertyName("ArcPoint3");
                    JsonSerializer.Serialize(writer, value.ArcPoint3, options);
                    break;
                case "Circle":
                    // writer.WritePropertyName("CircleCenter"); // Old property
                    // JsonSerializer.Serialize(writer, value.CircleCenter, options); // Old property
                    // writer.WriteNumber("CircleRadius", value.CircleRadius); // Old property
                    // writer.WritePropertyName("CircleNormal"); // Old property
                    // JsonSerializer.Serialize(writer, value.CircleNormal, options); // Old property
                    writer.WritePropertyName("CirclePoint1");
                    JsonSerializer.Serialize(writer, value.CirclePoint1, options);
                    writer.WritePropertyName("CirclePoint2");
                    JsonSerializer.Serialize(writer, value.CirclePoint2, options);
                    writer.WritePropertyName("CirclePoint3");
                    JsonSerializer.Serialize(writer, value.CirclePoint3, options);

                    // Serialize original circle parameters
                    writer.WritePropertyName("OriginalCircleCenter");
                    JsonSerializer.Serialize(writer, value.OriginalCircleCenter, options);
                    writer.WriteNumber("OriginalCircleRadius", value.OriginalCircleRadius);
                    writer.WritePropertyName("OriginalCircleNormal");
                    JsonSerializer.Serialize(writer, value.OriginalCircleNormal, options);
                    break;
            }

            writer.WriteEndObject();
        }
    }
}
