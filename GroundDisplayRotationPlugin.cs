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
        private readonly HashSet<HeadingInputHost> styledRotationInputs = new HashSet<HeadingInputHost>();
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
            var rotationRoot = new ToolStripMenuItem("Rotate View");
            ToolStripDropDownMenu rotationMenu = rotationRoot.DropDown as ToolStripDropDownMenu;
            if (rotationMenu != null)
            {
                rotationMenu.ShowImageMargin = false;
                rotationMenu.ShowCheckMargin = false;
            }

            var headingLabel = new ToolStripMenuItem("Magnetic Heading")
            {
                Enabled = false
            };
            var headingInput = new HeadingInputHost();
            headingInput.InputTextBox.Text = "000";
            headingInput.InputTextBox.Font = headingLabel.Font;
            headingInput.InputTextBox.KeyDown += HeadingInput_KeyDown;
            headingInput.InputTextBox.Leave += HeadingInput_Leave;
            headingInput.InputTextBox.Tag = headingInput;

            var resetRootItem = new ToolStripMenuItem("Reset to Original")
            {
                Tag = -1,
                CheckOnClick = false
            };
            resetRootItem.Click += RotationMenuItem_Click;

            rotationRoot.DropDownItems.Add(headingLabel);
            rotationRoot.DropDownItems.Add(headingInput);
            rotationRoot.DropDownItems.Add(new ToolStripSeparator());
            rotationRoot.DropDownItems.Add(resetRootItem);

            resetItems.Add(resetRootItem);
            rotationRoot.DropDownOpening += RotationRoot_DropDownOpening;
            rotationRoot.DropDownOpened += RotationRoot_DropDownOpened;

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

        private void RotationRoot_DropDownOpening(object sender, EventArgs e)
        {
            ToolStripMenuItem rotationRoot = sender as ToolStripMenuItem;
            if (rotationRoot == null)
            {
                return;
            }

            HeadingInputHost headingInput = GetHeadingInput(rotationRoot);
            if (headingInput == null)
            {
                return;
            }

            Form ownerForm = GetOwnerForm(rotationRoot);
            if (ownerForm == null)
            {
                return;
            }

            object groundControl = GetGroundControl(ownerForm);
            if (groundControl == null)
            {
                return;
            }

            int prefilledAngle;
            if (!TryGetCurrentMagneticHeading(groundControl, out prefilledAngle))
            {
                int savedAngle;
                if (lastAngleByGroundControl.TryGetValue(groundControl, out savedAngle))
                {
                    prefilledAngle = savedAngle;
                }
                else
                {
                    prefilledAngle = 0;
                }
            }

            headingInput.InputTextBox.Text = prefilledAngle.ToString("D3");
            EnsureHeadingInputStyled(ownerForm, rotationRoot, headingInput, false);
        }

        private void RotationRoot_DropDownOpened(object sender, EventArgs e)
        {
            ToolStripMenuItem rotationRoot = sender as ToolStripMenuItem;
            if (rotationRoot == null)
            {
                return;
            }

            HeadingInputHost headingInput = GetHeadingInput(rotationRoot);
            if (headingInput == null)
            {
                return;
            }

            Form ownerForm = GetOwnerForm(rotationRoot);
            if (ownerForm == null)
            {
                return;
            }

            EnsureHeadingInputStyled(ownerForm, rotationRoot, headingInput, true);
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

        private void HeadingInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
            {
                return;
            }

            TextBox textBox = sender as TextBox;
            HeadingInputHost headingInput = textBox == null ? null : textBox.Tag as HeadingInputHost;
            if (headingInput == null)
            {
                return;
            }

            ApplyRotationFromInlineInput(headingInput, true);
            RemoveFocusFromHeadingInput(headingInput);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void RemoveFocusFromHeadingInput(HeadingInputHost headingInput)
        {
            if (headingInput == null)
            {
                return;
            }

            Form ownerForm = GetOwnerForm(headingInput);
            if (ownerForm == null)
            {
                return;
            }

            object groundControl = GetGroundControl(ownerForm);
            Control groundDisplayControl = groundControl as Control;
            if (groundDisplayControl != null && !groundDisplayControl.IsDisposed && groundDisplayControl.CanFocus)
            {
                groundDisplayControl.BeginInvoke((MethodInvoker)delegate
                {
                    if (!groundDisplayControl.IsDisposed && groundDisplayControl.CanFocus)
                    {
                        groundDisplayControl.Focus();
                    }
                });
                return;
            }

            ownerForm.BeginInvoke((MethodInvoker)delegate
            {
                if (!ownerForm.IsDisposed && ownerForm.CanFocus)
                {
                    ownerForm.Focus();
                }
            });
        }

        private void HeadingInput_Leave(object sender, EventArgs e)
        {
            TextBox textBox = sender as TextBox;
            HeadingInputHost headingInput = textBox == null ? null : textBox.Tag as HeadingInputHost;
            if (headingInput == null)
            {
                return;
            }

            ApplyRotationFromInlineInput(headingInput, false);
        }

        private void ApplyRotationFromInlineInput(HeadingInputHost headingInput, bool notifyOnInvalidInput)
        {
            try
            {
                if (headingInput == null)
                {
                    return;
                }

                int selectedAngle;
                if (!TryParseAngle(headingInput.InputTextBox.Text, out selectedAngle))
                {
                    if (notifyOnInvalidInput)
                    {
                        MessageBox.Show("Please enter a whole number between 000 and 359.", "Ground Rotation Input");
                    }
                    return;
                }

                Form ownerForm = GetOwnerForm(headingInput);
                if (ownerForm == null)
                {
                    return;
                }

                object groundControl = GetGroundControl(ownerForm);
                if (groundControl == null)
                {
                    return;
                }

                lastAngleByGroundControl[groundControl] = selectedAngle;
                ApplyRotation(groundControl, selectedAngle);
                UpdateCheckedState(selectedAngle);

                headingInput.InputTextBox.Text = selectedAngle.ToString("D3");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ground rotation failed: " + ex.GetType().Name + ": " + ex.Message, "Ground Rotation Error");
            }
        }

        private HeadingInputHost GetHeadingInput(ToolStripMenuItem rotationRoot)
        {
            if (rotationRoot == null)
            {
                return null;
            }

            foreach (ToolStripItem item in rotationRoot.DropDownItems)
            {
                HeadingInputHost headingInput = item as HeadingInputHost;
                if (headingInput != null)
                {
                    return headingInput;
                }
            }

            return null;
        }

        private ToolStripMenuItem GetHeadingLabel(ToolStripMenuItem rotationRoot)
        {
            if (rotationRoot == null)
            {
                return null;
            }

            foreach (ToolStripItem item in rotationRoot.DropDownItems)
            {
                ToolStripMenuItem labelItem = item as ToolStripMenuItem;
                if (labelItem != null && string.Equals(labelItem.Text, "Magnetic Heading", StringComparison.Ordinal))
                {
                    return labelItem;
                }
            }

            return null;
        }

        private void EnsureHeadingInputStyled(Form ownerForm, ToolStripMenuItem rotationRoot, HeadingInputHost headingInput, bool force)
        {
            if (ownerForm == null || headingInput == null || rotationRoot == null)
            {
                return;
            }

            if (!force && styledRotationInputs.Contains(headingInput))
            {
                return;
            }

            ToolStripMenuItem toolsMenu = FindToolsMenu(ownerForm);
            if (toolsMenu == null)
            {
                return;
            }

            ToolStripMenuItem headingLabel = GetHeadingLabel(rotationRoot);
            Font labelFont = headingLabel != null ? headingLabel.Font : rotationRoot.Font;
            Color labelForeColor = headingLabel != null ? headingLabel.ForeColor : rotationRoot.DropDown.ForeColor;
            ToolStripTextBox referenceInput = FindReferenceTextBox(toolsMenu.DropDownItems, headingInput.InputTextBox);
            TextBox referenceControlInput = FindReferenceTextBoxControl(ownerForm, headingInput);
            headingInput.InputTextBox.Font = labelFont;

            if (referenceInput != null)
            {
                Color inputBackColor = referenceInput.TextBox == null ? referenceInput.BackColor : referenceInput.TextBox.BackColor;
                Color inputForeColor = referenceInput.TextBox == null ? referenceInput.ForeColor : referenceInput.TextBox.ForeColor;
                headingInput.ApplyStyle(inputBackColor, inputForeColor, ControlPaint.Dark(inputBackColor, 0.15f), labelFont);
            }
            else if (referenceControlInput != null)
            {
                headingInput.ApplyStyle(referenceControlInput.BackColor, referenceControlInput.ForeColor, ControlPaint.Dark(referenceControlInput.BackColor, 0.15f), labelFont);
            }
            else
            {
                headingInput.ApplyStyle(rotationRoot.DropDown.BackColor, labelForeColor, Color.FromArgb(70, 70, 70), labelFont);
            }

            styledRotationInputs.Add(headingInput);
        }

        private bool TryGetCurrentMagneticHeading(object groundControl, out int heading)
        {
            heading = 0;
            if (groundControl == null)
            {
                return false;
            }

            float renderRotationDegrees;
            if (!TryGetCurrentRenderRotation(groundControl, out renderRotationDegrees))
            {
                return false;
            }

            LogicalPositions.Position displayPosition;
            TryGetDisplayPosition(groundControl, out displayPosition);
            float magneticHeading = renderRotationDegrees;
            if (displayPosition != null)
            {
                magneticHeading += displayPosition.MagneticVariation;
            }

            heading = NormalizeHeading((int)Math.Round(magneticHeading, MidpointRounding.AwayFromZero));
            return true;
        }

        private static int NormalizeHeading(int heading)
        {
            int normalized = heading % 360;
            if (normalized < 0)
            {
                normalized += 360;
            }
            return normalized;
        }

        private TextBox FindReferenceTextBoxControl(Form ownerForm, HeadingInputHost rotationHeadingInput)
        {
            if (ownerForm == null)
            {
                return null;
            }

            TextBox fallback = null;
            TextBox styledFallback = null;
            TextBox findCandidate = null;

            foreach (Form form in Application.OpenForms)
            {
                if (form == null || form.IsDisposed)
                {
                    continue;
                }

                foreach (Control control in GetAllControlsIncludingRoot(form))
                {
                    TextBox textBox = control as TextBox;
                    if (textBox == null)
                    {
                        continue;
                    }

                    if (rotationHeadingInput != null && rotationHeadingInput.InputTextBox != null && ReferenceEquals(textBox, rotationHeadingInput.InputTextBox))
                    {
                        continue;
                    }

                    if (findCandidate == null && HasFindHint(textBox))
                    {
                        findCandidate = textBox;
                    }

                    if (styledFallback == null && (textBox.BackColor != SystemColors.Window || textBox.ForeColor != SystemColors.WindowText || textBox.BorderStyle != BorderStyle.Fixed3D))
                    {
                        styledFallback = textBox;
                    }

                    if (fallback == null)
                    {
                        fallback = textBox;
                    }
                }
            }

            if (findCandidate != null)
            {
                return findCandidate;
            }

            if (styledFallback != null)
            {
                return styledFallback;
            }

            return fallback;
        }

        private static bool HasFindHint(TextBox textBox)
        {
            if (textBox == null)
            {
                return false;
            }

            if (ContainsFind(textBox.Name) || ContainsFind(textBox.AccessibleName) || ContainsFind(textBox.Text))
            {
                return true;
            }

            Control parent = textBox.Parent;
            while (parent != null)
            {
                if (ContainsFind(parent.Name) || ContainsFind(parent.Text) || ContainsFind(parent.GetType().Name))
                {
                    return true;
                }
                parent = parent.Parent;
            }

            return false;
        }

        private static bool ContainsFind(string value)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf("find", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<Control> GetAllControlsIncludingRoot(Control root)
        {
            if (root == null)
            {
                yield break;
            }

            yield return root;
            foreach (Control child in GetAllControls(root))
            {
                yield return child;
            }
        }

        private ToolStripTextBox FindReferenceTextBox(ToolStripItemCollection items, TextBox rotationHeadingInput)
        {
            if (items == null)
            {
                return null;
            }

            foreach (ToolStripItem item in items)
            {
                ToolStripTextBox textBox = item as ToolStripTextBox;
                if (textBox != null && (rotationHeadingInput == null || textBox.TextBox == null || !ReferenceEquals(textBox.TextBox, rotationHeadingInput)))
                {
                    return textBox;
                }

                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem == null)
                {
                    continue;
                }

                ToolStripTextBox nestedTextBox = FindReferenceTextBox(menuItem.DropDownItems, rotationHeadingInput);
                if (nestedTextBox != null)
                {
                    return nestedTextBox;
                }
            }

            return null;
        }

        private bool TryParseAngle(string rawAngle, out int selectedAngle)
        {
            return int.TryParse((rawAngle ?? string.Empty).Trim(), out selectedAngle) && selectedAngle >= 0 && selectedAngle <= 359;
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

        private sealed class HeadingInputHost : ToolStripControlHost
        {
            public HeadingInputHost()
                : base(new BorderedInputControl())
            {
                AutoSize = false;
                Size = new Size(120, 26);
                Margin = new Padding(0, 1, 6, 3);
            }

            public TextBox InputTextBox
            {
                get { return InputControl.InnerTextBox; }
            }

            public Color InputBackColor
            {
                get { return InputControl.InputBackColor; }
            }

            public bool IsFocused
            {
                get { return InputControl.IsInputFocused; }
            }

            private BorderedInputControl InputControl
            {
                get { return (BorderedInputControl)Control; }
            }

            public void ApplyStyle(Color inputBackColor, Color inputForeColor, Color borderColor, Font font)
            {
                InputControl.ApplyStyle(inputBackColor, inputForeColor, borderColor, font);
            }
        }

        private sealed class BorderedInputControl : Control
        {
            private readonly TextBox innerTextBox = new TextBox();
            private Color borderColor = Color.Black;
            private readonly Color focusBorderColor = Color.FromArgb(0, 232, 255);
            private bool hasFocus;

            public BorderedInputControl()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = SystemColors.Control;
                Size = new Size(120, 26);

                innerTextBox.BorderStyle = BorderStyle.None;
                innerTextBox.Location = new Point(5, 4);
                innerTextBox.Width = Width - 8;
                innerTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
                innerTextBox.Enter += InnerTextBox_FocusChanged;
                innerTextBox.Leave += InnerTextBox_FocusChanged;
                innerTextBox.GotFocus += InnerTextBox_FocusChanged;
                innerTextBox.LostFocus += InnerTextBox_FocusChanged;
                innerTextBox.MouseDown += InnerTextBox_FocusChanged;
                innerTextBox.KeyDown += InnerTextBox_FocusChanged;
                innerTextBox.Enter += InnerTextBox_SelectAll;
                innerTextBox.GotFocus += InnerTextBox_SelectAll;
                Controls.Add(innerTextBox);
            }

            public bool IsInputFocused
            {
                get { return hasFocus; }
            }

            private void InnerTextBox_FocusChanged(object sender, EventArgs e)
            {
                UpdateFocusState();
            }

            private void InnerTextBox_SelectAll(object sender, EventArgs e)
            {
                if (innerTextBox.IsDisposed)
                {
                    return;
                }

                innerTextBox.BeginInvoke((MethodInvoker)delegate
                {
                    if (!innerTextBox.IsDisposed)
                    {
                        innerTextBox.SelectAll();
                    }
                });
            }

            protected override void OnGotFocus(EventArgs e)
            {
                base.OnGotFocus(e);
                UpdateFocusState();
            }

            protected override void OnLostFocus(EventArgs e)
            {
                base.OnLostFocus(e);
                UpdateFocusState();
            }

            public TextBox InnerTextBox
            {
                get { return innerTextBox; }
            }

            public Color InputBackColor
            {
                get { return innerTextBox.BackColor; }
            }

            public void ApplyStyle(Color inputBackColor, Color inputForeColor, Color outerBorderColor, Font font)
            {
                borderColor = outerBorderColor;
                BackColor = inputBackColor;
                innerTextBox.BackColor = inputBackColor;
                innerTextBox.ForeColor = inputForeColor;
                innerTextBox.Font = font;
                UpdateFocusState();
                Invalidate();
            }

            private void UpdateFocusState()
            {
                bool focusedNow = innerTextBox.Focused || innerTextBox.ContainsFocus || Focused || ContainsFocus;
                if (focusedNow != hasFocus)
                {
                    hasFocus = focusedNow;
                    Invalidate();
                }
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                innerTextBox.Location = new Point(5, Math.Max(2, (Height - innerTextBox.PreferredHeight) / 2));
                innerTextBox.Width = Math.Max(10, Width - 8);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (SolidBrush brush = new SolidBrush(BackColor))
                {
                    e.Graphics.FillRectangle(brush, ClientRectangle);
                }

                Color paintBorder = hasFocus ? focusBorderColor : borderColor;
                using (Pen pen = new Pen(paintBorder))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                }
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
