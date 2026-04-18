using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        private readonly List<ToolStripMenuItem> resetItems = new List<ToolStripMenuItem>();
        private readonly Dictionary<object, float> originalRotation = new Dictionary<object, float>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, Dictionary<object, float>> originalRotationByPosition = new Dictionary<object, Dictionary<object, float>>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, int> lastAngleByGroundControl = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Form, ToolStripMenuItem> rotationMenusByForm = new Dictionary<Form, ToolStripMenuItem>();
        private readonly Timer menuInjectionTimer = new Timer();

        public string Name
        {
            get { return "Ground Display Rotation"; }
        }

        public GroundDisplayRotationPlugin()
        {
            StartMenuInjection();
        }

        private void StartMenuInjection()
        {
            menuInjectionTimer.Interval = 1000;
            menuInjectionTimer.Tick += MenuInjectionTimer_Tick;
            menuInjectionTimer.Start();
            EnsureMenusInjected();
        }

        private void MenuInjectionTimer_Tick(object sender, EventArgs e)
        {
            EnsureMenusInjected();
        }

        private void EnsureMenusInjected()
        {
            try
            {
                foreach (Form form in Application.OpenForms)
                {
                    if (form == null || form.IsDisposed)
                    {
                        continue;
                    }

                    if (!IsGroundWindowForm(form))
                    {
                        continue;
                    }

                    object groundControl = GetGroundControl(form);
                    if (groundControl == null)
                    {
                        continue;
                    }

                    ToolStripMenuItem toolsMenu = FindToolsMenu(form);
                    if (toolsMenu == null)
                    {
                        continue;
                    }

                    ToolStripMenuItem rotationRoot;
                    if (!rotationMenusByForm.TryGetValue(form, out rotationRoot) || rotationRoot == null || rotationRoot.IsDisposed)
                    {
                        rotationRoot = CreateRotationRootMenuItem();
                        rotationMenusByForm[form] = rotationRoot;
                        form.FormClosed += GroundForm_FormClosed;
                    }

                    bool alreadyAttached = false;
                    foreach (ToolStripItem item in toolsMenu.DropDownItems)
                    {
                        if (ReferenceEquals(item, rotationRoot))
                        {
                            alreadyAttached = true;
                            break;
                        }
                    }

                    if (!alreadyAttached)
                    {
                        toolsMenu.DropDownItems.Add(rotationRoot);
                    }
                }
            }
            catch
            {
                // Never allow UI/menu injection issues to break vatSys.
            }
        }

        private void GroundForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            Form form = sender as Form;
            if (form == null)
            {
                return;
            }

            object groundControl = GetGroundControl(form);
            if (groundControl != null)
            {
                originalRotation.Remove(groundControl);
                originalRotationByPosition.Remove(groundControl);
                lastAngleByGroundControl.Remove(groundControl);
            }

            rotationMenusByForm.Remove(form);
        }

        private static bool IsGroundWindowForm(Form form)
        {
            if (form == null)
            {
                return false;
            }

            Type formType = form.GetType();
            return formType.Name == "ASMGCSWindow" || formType.FullName == "vatsys.ASMGCSWindow";
        }

        private ToolStripMenuItem FindToolsMenu(Form form)
        {
            FieldInfo toolsField = form.GetType().GetField("toolsToolStripMenuItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (toolsField != null)
            {
                ToolStripMenuItem toolsFromField = toolsField.GetValue(form) as ToolStripMenuItem;
                if (toolsFromField != null)
                {
                    return toolsFromField;
                }
            }

            foreach (Control control in GetAllControls(form))
            {
                MenuStrip menuStrip = control as MenuStrip;
                if (menuStrip == null)
                {
                    continue;
                }

                foreach (ToolStripItem item in menuStrip.Items)
                {
                    ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                    if (menuItem == null)
                    {
                        continue;
                    }

                    string normalizedText = menuItem.Text == null ? string.Empty : menuItem.Text.Replace("&", string.Empty);
                    if (string.Equals(normalizedText, "Tools", StringComparison.OrdinalIgnoreCase))
                    {
                        return menuItem;
                    }
                }
            }

            return null;
        }

        private static IEnumerable<Control> GetAllControls(Control root)
        {
            if (root == null)
            {
                yield break;
            }

            foreach (Control child in root.Controls)
            {
                yield return child;

                foreach (Control descendant in GetAllControls(child))
                {
                    yield return descendant;
                }
            }
        }

        private ToolStripMenuItem CreateRotationRootMenuItem()
        {
            var rotationRoot = new ToolStripMenuItem("Ground Rotation");

            var setAngleRootItem = new ToolStripMenuItem("Set rotation angle...")
            {
                CheckOnClick = false
            };
            setAngleRootItem.Click += SetRotationAngleMenuItem_Click;

            var resetRootItem = new ToolStripMenuItem("Reset to original")
            {
                Tag = -1,
                CheckOnClick = false
            };
            resetRootItem.Click += RotationMenuItem_Click;

            rotationRoot.DropDownItems.Add(setAngleRootItem);
            rotationRoot.DropDownItems.Add(new ToolStripSeparator());
            rotationRoot.DropDownItems.Add(resetRootItem);
            resetItems.Add(resetRootItem);

            return rotationRoot;
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

                ToolStripDropDown dropDown = clickedItem.Owner as ToolStripDropDown;
                if (dropDown == null)
                {
                    dropDown = clickedItem.GetCurrentParent() as ToolStripDropDown;
                }
                if (dropDown != null)
                {
                    dropDown.Close(ToolStripDropDownCloseReason.AppClicked);
                }

                Control control = groundControl as Control;
                if (control != null && control.CanFocus)
                {
                    control.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!control.IsDisposed && control.CanFocus)
                        {
                            control.Focus();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ground rotation failed: " + ex.GetType().Name + ": " + ex.Message, "Ground Rotation Error");
            }
        }

        private void SetRotationAngleMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                ToolStripMenuItem clickedItem = sender as ToolStripMenuItem;
                if (clickedItem == null)
                {
                    return;
                }

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

                int prefilledAngle = 0;
                int savedAngle;
                if (lastAngleByGroundControl.TryGetValue(groundControl, out savedAngle))
                {
                    prefilledAngle = savedAngle;
                }

                int selectedAngle;
                if (!TryPromptForAngle(prefilledAngle, out selectedAngle))
                {
                    return;
                }

                lastAngleByGroundControl[groundControl] = selectedAngle;
                ApplyRotation(groundControl, selectedAngle);
                UpdateCheckedState(selectedAngle);

                ToolStripDropDown dropDown = clickedItem.GetCurrentParent() as ToolStripDropDown;
                if (dropDown != null)
                {
                    dropDown.Close(ToolStripDropDownCloseReason.ItemClicked);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ground rotation failed: " + ex.GetType().Name + ": " + ex.Message, "Ground Rotation Error");
            }
        }

        private bool TryPromptForAngle(int initialAngle, out int selectedAngle)
        {
            selectedAngle = initialAngle;

            using (Form prompt = new Form())
            using (TextBox input = new TextBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                prompt.Text = "Ground Rotation Angle";
                prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
                prompt.StartPosition = FormStartPosition.CenterParent;
                prompt.MinimizeBox = false;
                prompt.MaximizeBox = false;
                prompt.ClientSize = new Size(240, 92);
                prompt.ShowInTaskbar = false;

                Label label = new Label();
                label.Text = "Enter angle (0-359):";
                label.AutoSize = true;
                label.Location = new Point(10, 10);

                input.Location = new Point(12, 30);
                input.Width = 214;
                input.Text = initialAngle.ToString("D3");

                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.Location = new Point(70, 60);

                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Location = new Point(145, 60);

                prompt.Controls.Add(label);
                prompt.Controls.Add(input);
                prompt.Controls.Add(ok);
                prompt.Controls.Add(cancel);
                prompt.AcceptButton = ok;
                prompt.CancelButton = cancel;

                if (prompt.ShowDialog() != DialogResult.OK)
                {
                    return false;
                }

                int parsedAngle;
                if (!int.TryParse(input.Text.Trim(), out parsedAngle) || parsedAngle < 0 || parsedAngle > 359)
                {
                    MessageBox.Show("Please enter a whole number between 0 and 359.", "Ground Rotation Input");
                    return false;
                }

                selectedAngle = parsedAngle;
                return true;
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

        private static string GetPositionKey(LogicalPositions.Position displayPosition)
        {
            if (displayPosition == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(displayPosition.Name))
            {
                return displayPosition.Name;
            }

            return displayPosition.GetHashCode().ToString();
        }

        private void CaptureOriginalRotationForPosition(object groundControl, LogicalPositions.Position displayPosition, float renderRotationDegrees)
        {
            if (groundControl == null || displayPosition == null)
            {
                return;
            }

            Dictionary<object, float> perPositionRotations;
            if (!originalRotationByPosition.TryGetValue(groundControl, out perPositionRotations))
            {
                perPositionRotations = new Dictionary<object, float>(ReferenceEqualityComparer.Instance);
                originalRotationByPosition[groundControl] = perPositionRotations;
            }

            if (!perPositionRotations.ContainsKey(displayPosition))
            {
                perPositionRotations[displayPosition] = renderRotationDegrees;
            }
        }

        private bool TryGetOriginalRotationForPosition(object groundControl, LogicalPositions.Position displayPosition, out float rotation)
        {
            rotation = 0f;
            if (groundControl == null || displayPosition == null)
            {
                return false;
            }

            Dictionary<object, float> perPositionRotations;
            if (!originalRotationByPosition.TryGetValue(groundControl, out perPositionRotations) || perPositionRotations == null)
            {
                return false;
            }

            return perPositionRotations.TryGetValue(displayPosition, out rotation);
        }

        private void ApplyRotation(object groundControl, int angle)
        {
            LogicalPositions.Position displayPosition = null;
            TryGetDisplayPosition(groundControl, out displayPosition);
            float currentRenderRotation = 0f;
            bool hasCurrentRenderRotation = TryGetCurrentRenderRotation(groundControl, out currentRenderRotation);

            if (hasCurrentRenderRotation && !originalRotation.ContainsKey(groundControl))
            {
                originalRotation[groundControl] = currentRenderRotation;
            }

            if (displayPosition != null && hasCurrentRenderRotation)
            {
                CaptureOriginalRotationForPosition(groundControl, displayPosition, currentRenderRotation);
            }

            if (!originalRotation.ContainsKey(groundControl) && displayPosition != null)
            {
                originalRotation[groundControl] = displayPosition.Rotation;
            }

            if (displayPosition == null)
            {
                MethodInfo loadPositionMethod = groundControl.GetType().GetMethod("LoadPosition", BindingFlags.Instance | BindingFlags.Public);
                if (loadPositionMethod != null)
                {
                    loadPositionMethod.Invoke(groundControl, new object[] { null });
                }
            }

            if (angle < 0)
            {
                float newRotation = 0f;
                float savedRotation;
                if (TryGetOriginalRotationForPosition(groundControl, displayPosition, out savedRotation))
                {
                    newRotation = savedRotation;
                }
                else if (originalRotation.TryGetValue(groundControl, out savedRotation))
                {
                    newRotation = savedRotation;
                }

                SetRenderRotation(groundControl, newRotation, displayPosition);

                Control control = groundControl as Control;
                if (control != null)
                {
                    control.Invalidate();
                    control.Update();
                }
                return;
            }

            float newRotationApplied = angle;
            if (displayPosition != null)
            {
                newRotationApplied -= displayPosition.MagneticVariation;
            }

            SetRenderRotation(groundControl, newRotationApplied, displayPosition);

            Control targetControl = groundControl as Control;
            if (targetControl != null)
            {
                targetControl.Invalidate();
                targetControl.Update();
            }
        }

        private bool TryGetDisplayPosition(object groundControl, out LogicalPositions.Position displayPosition)
        {
            displayPosition = null;
            if (groundControl == null)
            {
                return false;
            }

            PropertyInfo displayPositionProperty = groundControl.GetType().GetProperty("DisplayPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (displayPositionProperty != null)
            {
                object displayPositionValue = displayPositionProperty.GetValue(groundControl);
                displayPosition = displayPositionValue as LogicalPositions.Position;
            }
            if (displayPosition == null)
            {
                FieldInfo displayPositionField = groundControl.GetType().GetField("displayPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (displayPositionField != null)
                {
                    object displayPositionValue = displayPositionField.GetValue(groundControl);
                    displayPosition = displayPositionValue as LogicalPositions.Position;
                }
            }

            return displayPosition != null;
        }

        private bool TryGetCurrentRenderRotation(object groundControl, out float rotationDegrees)
        {
            rotationDegrees = 0f;
            if (groundControl == null)
            {
                return false;
            }

            MethodInfo getRenderParamsMethod = groundControl.GetType().GetMethod("GetRenderParams", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { typeof(bool) }, null);
            if (getRenderParamsMethod == null)
            {
                getRenderParamsMethod = groundControl.GetType().GetMethod("GetRenderParams", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            }
            if (getRenderParamsMethod == null)
            {
                return false;
            }

            object[] getRenderParamsArgs = getRenderParamsMethod.GetParameters().Length == 0 ? null : new object[] { false };
            object renderParams = getRenderParamsMethod.Invoke(groundControl, getRenderParamsArgs);
            if (renderParams == null)
            {
                return false;
            }

            PropertyInfo rotationProperty = renderParams.GetType().GetProperty("Rotation");
            if (rotationProperty == null)
            {
                return false;
            }

            object rotationValue = rotationProperty.GetValue(renderParams);
            if (rotationValue == null)
            {
                return false;
            }

            double rotationRadians = Convert.ToDouble(rotationValue);
            rotationDegrees = (float)(-Conversions.RadiansToDegrees(rotationRadians));
            return true;
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
            effectiveRotation = -effectiveRotation;

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
            foreach (ToolStripMenuItem resetItem in resetItems)
            {
                resetItem.Checked = false;
            }
        }

        private class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

    }
}
