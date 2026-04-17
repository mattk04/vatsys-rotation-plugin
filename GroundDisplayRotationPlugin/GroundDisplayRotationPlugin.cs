using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;

namespace GroundDisplayRotationPlugin
{
    /// <summary>
    /// vatSys plugin that adds a Ground Rotation dropdown to ASMGCS (ground map) Tools menus.
    /// The plugin updates the current ground display position rotation and reloads the display.
    /// </summary>
    [Export(typeof(IPlugin))]
    public class GroundDisplayRotationPlugin : IPlugin
    {
        private readonly List<ToolStripMenuItem> rotationItems = new List<ToolStripMenuItem>();
        private readonly List<ToolStripMenuItem> resetItems = new List<ToolStripMenuItem>();
        private readonly List<ToolStripMenuItem> debugItems = new List<ToolStripMenuItem>();
        private readonly Dictionary<LogicalPositions.Position, float> originalRotation = new Dictionary<LogicalPositions.Position, float>();
        private readonly int[] allowedAngles = new int[] { 0, 45, 90, 135, 180, 225, 270, 315 };

        public string Name
        {
            get { return "Ground Display Rotation"; }
        }

        public GroundDisplayRotationPlugin()
        {
            AddRotationMenuForWindowType(CustomToolStripMenuItemWindowType.ASMGCS);
        }

        private void AddRotationMenuForWindowType(CustomToolStripMenuItemWindowType windowType)
        {
            var rotationRoot = new ToolStripMenuItem("Ground Rotation");

            foreach (int angle in allowedAngles)
            {
                var item = new ToolStripMenuItem(string.Format("{0}°", angle))
                {
                    Tag = angle,
                    CheckOnClick = false
                };
                item.Click += RotationMenuItem_Click;
                rotationRoot.DropDownItems.Add(item);
                rotationItems.Add(item);
            }

            rotationRoot.DropDownItems.Add(new ToolStripSeparator());
            var resetRootItem = new ToolStripMenuItem("Reset to original")
            {
                Tag = -1,
                CheckOnClick = false
            };
            resetRootItem.Click += RotationMenuItem_Click;
            rotationRoot.DropDownItems.Add(resetRootItem);
            resetItems.Add(resetRootItem);

            var debugRootItem = new ToolStripMenuItem("Debug ground rotation")
            {
                Tag = -2,
                CheckOnClick = false
            };
            debugRootItem.Click += DebugRotationState_Click;
            rotationRoot.DropDownItems.Add(new ToolStripSeparator());
            rotationRoot.DropDownItems.Add(debugRootItem);
            debugItems.Add(debugRootItem);

            MMI.AddCustomMenuItem(new CustomToolStripMenuItem(windowType, CustomToolStripMenuItemCategory.Tools, rotationRoot));
        }

        public void OnFDRUpdate(FDP2.FDR updated)
        {
            // Nothing required for this plugin on flight data updates.
        }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated)
        {
            // Nothing required for this plugin on radar track updates.
        }

        private void RotationMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                var clickedItem = sender as ToolStripMenuItem;
                if (clickedItem == null)
                {
                    return;
                }

                object tag = clickedItem.Tag;
                if (!(tag is int))
                {
                    return;
                }
                int selectedAngle = (int)tag;

                Form ownerForm = GetOwnerForm(clickedItem);
                if (ownerForm == null)
                {
                    return;
                }

                object groundControl = GetGroundControl(ownerForm);
                if (groundControl == null)
                {
                    return;
                }

                ApplyRotation(groundControl, selectedAngle);
                UpdateCheckedState(selectedAngle);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ground rotation failed: " + ex.GetType().Name + ": " + ex.Message, "Ground Rotation Error");
            }
        }

        private Form GetOwnerForm(ToolStripItem item)
        {
            if (item == null)
            {
                return null;
            }

            ToolStrip toolStrip = item.GetCurrentParent();
            if (toolStrip == null)
            {
                ToolStrip ownerToolStrip = item.Owner as ToolStrip;
                if (ownerToolStrip != null)
                {
                    toolStrip = ownerToolStrip;
                }
            }

            if (toolStrip != null)
            {
                Form form = toolStrip.FindForm();
                if (form != null)
                {
                    return form;
                }

                ToolStripDropDownMenu dropDownMenu = toolStrip as ToolStripDropDownMenu;
                if (dropDownMenu != null && dropDownMenu.OwnerItem != null)
                {
                    ToolStripItem ownerItem = dropDownMenu.OwnerItem;
                    while (ownerItem != null)
                    {
                        ToolStrip ownerToolStrip = ownerItem.Owner as ToolStrip;
                        if (ownerToolStrip != null)
                        {
                            form = ownerToolStrip.FindForm();
                            if (form != null)
                            {
                                return form;
                            }
                        }
                        ownerItem = ownerItem.OwnerItem;
                    }
                }
            }

            ToolStripItem currentItem = item;
            while (currentItem != null)
            {
                ToolStrip ownerToolStrip = currentItem.Owner as ToolStrip;
                if (ownerToolStrip != null)
                {
                    Form form = ownerToolStrip.FindForm();
                    if (form != null)
                    {
                        return form;
                    }
                }
                currentItem = currentItem.OwnerItem;
            }

            return null;
        }

        private object GetGroundControl(Form ownerForm)
        {
            Type ownerType = ownerForm.GetType();

            // Prefer a dedicated ASMGCS control field if present.
            FieldInfo groundField = ownerType.GetField("asmgcsControlDX1", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (groundField != null)
            {
                object value = groundField.GetValue(ownerForm);
                if (IsAsdControl(value))
                {
                    return value;
                }
            }

            // Search all fields for an ASDControlDX instance.
            foreach (FieldInfo field in ownerType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                object value = field.GetValue(ownerForm);
                if (IsAsdControl(value))
                {
                    return value;
                }
            }

            // Search properties as a last resort.
            foreach (PropertyInfo property in ownerType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }
                object value;
                try
                {
                    value = property.GetValue(ownerForm);
                }
                catch
                {
                    continue;
                }
                if (IsAsdControl(value))
                {
                    return value;
                }
            }

            return null;
        }

        private bool IsAsdControl(object value)
        {
            if (value == null)
            {
                return false;
            }
            Type type = value.GetType();
            return type.Name == "ASDControlDX" || type.FullName == "vatsys.ASDControlDX";
        }

        private void ApplyRotation(object groundControl, int angle)
        {
            PropertyInfo displayPositionProperty = groundControl.GetType().GetProperty("DisplayPosition", BindingFlags.Instance | BindingFlags.Public);
            LogicalPositions.Position displayPosition = null;
            if (displayPositionProperty != null)
            {
                object displayPositionValue = displayPositionProperty.GetValue(groundControl);
                displayPosition = displayPositionValue as LogicalPositions.Position;
                if (displayPosition != null && !originalRotation.ContainsKey(displayPosition))
                {
                    originalRotation[displayPosition] = displayPosition.Rotation;
                }
            }

            if (displayPosition == null)
            {
                MethodInfo loadPositionMethod = groundControl.GetType().GetMethod("LoadPosition", BindingFlags.Instance | BindingFlags.Public);
                if (loadPositionMethod != null)
                {
                    loadPositionMethod.Invoke(groundControl, new object[] { null });
                }
            }

            float newRotation = angle;
            if (angle < 0)
            {
                if (displayPosition != null)
                {
                    float savedRotation;
                    if (originalRotation.TryGetValue(displayPosition, out savedRotation))
                    {
                        newRotation = savedRotation;
                    }
                    else
                    {
                        newRotation = displayPosition.Rotation;
                    }
                }
                else
                {
                    newRotation = 0f;
                }
            }

            if (displayPosition != null)
            {
                displayPosition.Rotation = newRotation;
            }

            SetRenderRotation(groundControl, newRotation, displayPosition);

            Control control = groundControl as Control;
            if (control != null)
            {
                control.Invalidate();
                control.Update();
            }
        }

        private void DebugRotationState_Click(object sender, EventArgs e)
        {
            try
            {
                var clickedItem = sender as ToolStripMenuItem;
                if (clickedItem == null)
                {
                    return;
                }

                Form ownerForm = GetOwnerForm(clickedItem);
                if (ownerForm == null)
                {
                    MessageBox.Show("Could not find owning form.", "Ground Rotation Debug");
                    return;
                }

                object groundControl = GetGroundControl(ownerForm);
                if (groundControl == null)
                {
                    MessageBox.Show("Could not find ASMGCS ground control.", "Ground Rotation Debug");
                    return;
                }

                string message = "Owner form: " + ownerForm.GetType().FullName + "\r\n";
                message += "Ground control: " + groundControl.GetType().FullName + "\r\n";

                PropertyInfo displayPositionProperty = groundControl.GetType().GetProperty("DisplayPosition", BindingFlags.Instance | BindingFlags.Public);
                if (displayPositionProperty != null)
                {
                    object displayPositionValue = displayPositionProperty.GetValue(groundControl);
                    LogicalPositions.Position displayPosition = displayPositionValue as LogicalPositions.Position;
                    if (displayPosition != null)
                    {
                        message += "Display position: " + displayPosition.Name + "\r\n";
                        message += "Display position rotation: " + displayPosition.Rotation + "\r\n";
                    }
                    else
                    {
                        message += "Display position: null\r\n";
                    }
                }
                else
                {
                    message += "DisplayPosition property not found.\r\n";
                }

                MethodInfo getRenderParamsMethod = groundControl.GetType().GetMethod("GetRenderParams", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { typeof(bool) }, null);
                if (getRenderParamsMethod == null)
                {
                    getRenderParamsMethod = groundControl.GetType().GetMethod("GetRenderParams", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                }
                message += "GetRenderParams found: " + (getRenderParamsMethod != null) + "\r\n";
                if (getRenderParamsMethod != null)
                {
                    object[] getRenderParamsArgs = getRenderParamsMethod.GetParameters().Length == 0 ? null : new object[] { false };
                    object renderParams = getRenderParamsMethod.Invoke(groundControl, getRenderParamsArgs);
                    if (renderParams != null)
                    {
                        Type renderParamsType = renderParams.GetType();
                        PropertyInfo rotationProperty = renderParamsType.GetProperty("Rotation");
                        if (rotationProperty != null)
                        {
                            object rotationValue = rotationProperty.GetValue(renderParams);
                            double rotationDouble = rotationValue == null ? double.NaN : Convert.ToDouble(rotationValue);
                            message += "Render rotation rad: " + rotationValue + "\r\n";
                            message += "Render rotation deg: " + Conversions.RadiansToDegrees(rotationDouble) + "\r\n";
                        }
                        else
                        {
                            message += "Rotation property not found on render params.\r\n";
                        }
                    }
                    else
                    {
                        message += "GetRenderParams returned null.\r\n";
                    }
                }

                MethodInfo setRenderParamsMethod = null;
                foreach (MethodInfo method in groundControl.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (method.Name == "SetRenderParams" && method.GetParameters().Length == 10)
                    {
                        setRenderParamsMethod = method;
                        break;
                    }
                }
                message += "SetRenderParams found: " + (setRenderParamsMethod != null) + "\r\n";

                MessageBox.Show(message, "Ground Rotation Debug");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ground rotation debug failed: " + ex.GetType().Name + ": " + ex.Message, "Ground Rotation Debug");
            }
        }

        private void SetRenderRotation(object groundControl, float rotationDegrees, LogicalPositions.Position displayPosition)
        {
            MethodInfo getRenderParamsMethod = groundControl.GetType().GetMethod("GetRenderParams", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { typeof(bool) }, null);
            if (getRenderParamsMethod == null)
            {
                getRenderParamsMethod = groundControl.GetType().GetMethod("GetRenderParams", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            }
            if (getRenderParamsMethod == null)
            {
                return;
            }

            object[] getRenderParamsArgs = getRenderParamsMethod.GetParameters().Length == 0 ? null : new object[] { false };
            object renderParams = getRenderParamsMethod.Invoke(groundControl, getRenderParamsArgs);
            if (renderParams == null)
            {
                return;
            }

            Type renderParamsType = renderParams.GetType();
            object screenCentre = renderParamsType.GetProperty("ScreenCentre").GetValue(renderParams);
            double viewScale = (double)renderParamsType.GetProperty("ViewScale").GetValue(renderParams);
            double zoom = (double)renderParamsType.GetProperty("Zoom").GetValue(renderParams);
            object clientSize = renderParamsType.GetProperty("ClientSize").GetValue(renderParams);
            bool updateColours = (bool)renderParamsType.GetProperty("UpdateColours").GetValue(renderParams);
            bool flash = (bool)renderParamsType.GetProperty("Flash").GetValue(renderParams);
            bool timeshare = (bool)renderParamsType.GetProperty("Timeshare").GetValue(renderParams);
            uint[] visibleMaps = (uint[])renderParamsType.GetProperty("VisibleMaps").GetValue(renderParams);

            float effectiveRotation = rotationDegrees;
            if (displayPosition != null)
            {
                effectiveRotation += displayPosition.MagneticVariation;
            }

            MethodInfo setRenderParamsMethod = null;
            foreach (MethodInfo method in groundControl.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (method.Name == "SetRenderParams" && method.GetParameters().Length == 10)
                {
                    setRenderParamsMethod = method;
                    break;
                }
            }
            if (setRenderParamsMethod == null)
            {
                return;
            }

            float rotationRadians = (float)Conversions.DegreesToRadians(effectiveRotation);
            setRenderParamsMethod.Invoke(groundControl, new object[]
            {
                screenCentre,
                viewScale,
                zoom,
                clientSize,
                rotationRadians,
                new bool?(true),
                new bool?(updateColours),
                new bool?(flash),
                new bool?(timeshare),
                visibleMaps
            });
        }

        private void UpdateCheckedState(int selectedAngle)
        {
            foreach (ToolStripMenuItem item in rotationItems)
            {
                object tag = item.Tag;
                if (tag is int)
                {
                    int angle = (int)tag;
                    item.Checked = angle == selectedAngle;
                }
            }
            foreach (ToolStripMenuItem resetItem in resetItems)
            {
                resetItem.Checked = selectedAngle < 0;
            }
        }
    }
}
