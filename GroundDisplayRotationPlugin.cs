using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
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
        private const string SavedHeadingsMenuKey = "SavedHeadingsMenu";
        private const string ClearAutoApplyMenuKey = "ClearAutoApplyMenu";

        private readonly List<ToolStripMenuItem> resetItems = new List<ToolStripMenuItem>();
        private readonly Dictionary<object, float> originalRotation = new Dictionary<object, float>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, Dictionary<object, float>> originalRotationByPosition = new Dictionary<object, Dictionary<object, float>>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, int> lastAngleByGroundControl = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Form, ToolStripMenuItem> rotationMenusByForm = new Dictionary<Form, ToolStripMenuItem>();
        private readonly Dictionary<object, string> lastAerodromeByGroundControl = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        private readonly RotationSettingsStore rotationStore = new RotationSettingsStore();
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

                    HandleAutoApplyForGroundControl(groundControl);

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
                lastAerodromeByGroundControl.Remove(groundControl);
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

            var applyLabel = new ApplyLabelHost(headingInput);
            applyLabel.ApplyLabel.Click += ApplyLabel_Click;

            var resetRootItem = new ToolStripMenuItem("Reset to Original Heading")
            {
                Tag = -1,
                CheckOnClick = false
            };
            resetRootItem.Click += RotationMenuItem_Click;

            var savedHeadingsItem = new ToolStripMenuItem("Saved Headings")
            {
                Name = SavedHeadingsMenuKey
            };
            ToolStripDropDownMenu savedHeadingsMenu = savedHeadingsItem.DropDown as ToolStripDropDownMenu;
            if (savedHeadingsMenu != null)
            {
                savedHeadingsMenu.ShowImageMargin = false;
                savedHeadingsMenu.ShowCheckMargin = false;
            }

            var saveHeadingItem = new ToolStripMenuItem("Save Current Heading");
            saveHeadingItem.Click += SaveHeadingItem_Click;

            var clearAutoApplyItem = new ToolStripMenuItem("Disable Auto-Load")
            {
                Name = ClearAutoApplyMenuKey
            };
            clearAutoApplyItem.Click += ClearAutoApplyItem_Click;

            rotationRoot.DropDownItems.Add(headingLabel);
            rotationRoot.DropDownItems.Add(headingInput);
            rotationRoot.DropDownItems.Add(applyLabel);
            rotationRoot.DropDownItems.Add(new ToolStripSeparator());
            rotationRoot.DropDownItems.Add(saveHeadingItem);
            rotationRoot.DropDownItems.Add(savedHeadingsItem);
            rotationRoot.DropDownItems.Add(clearAutoApplyItem);
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
            PopulateSavedHeadingsMenu(rotationRoot, groundControl);
            UpdateAutoApplyMenuState(rotationRoot, groundControl);
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

        private void ApplyLabel_Click(object sender, EventArgs e)
        {
            Label label = sender as Label;
            ApplyLabelHost applyLabelHost = label == null ? null : label.Tag as ApplyLabelHost;
            if (applyLabelHost == null || applyLabelHost.HeadingInput == null)
            {
                return;
            }

            bool applied = ApplyRotationFromInlineInput(applyLabelHost.HeadingInput, true);
            if (!applied)
            {
                return;
            }

            RemoveFocusFromHeadingInput(applyLabelHost.HeadingInput);

            ToolStripDropDown dropDown = applyLabelHost.Owner as ToolStripDropDown;
            if (dropDown == null)
            {
                dropDown = applyLabelHost.GetCurrentParent() as ToolStripDropDown;
            }
            CloseDropDownHierarchy(dropDown);
        }

        private static void CloseDropDownHierarchy(ToolStripDropDown dropDown)
        {
            ToolStripDropDown current = dropDown;
            while (current != null)
            {
                ToolStripItem ownerItem = current.OwnerItem;
                current.Close(ToolStripDropDownCloseReason.AppClicked);
                current = ownerItem == null ? null : ownerItem.Owner as ToolStripDropDown;
            }
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

        private bool ApplyRotationFromInlineInput(HeadingInputHost headingInput, bool notifyOnInvalidInput)
        {
            try
            {
                if (headingInput == null)
                {
                    return false;
                }

                int selectedAngle;
                if (!TryParseAngle(headingInput.InputTextBox.Text, out selectedAngle))
                {
                    if (notifyOnInvalidInput)
                    {
                        MessageBox.Show("Please enter a whole number between 000 and 359.", "Ground Rotation Input");
                    }
                    return false;
                }

                Form ownerForm = GetOwnerForm(headingInput);
                if (ownerForm == null)
                {
                    return false;
                }

                object groundControl = GetGroundControl(ownerForm);
                if (groundControl == null)
                {
                    return false;
                }

                lastAngleByGroundControl[groundControl] = selectedAngle;
                ApplyRotation(groundControl, selectedAngle);
                UpdateCheckedState(selectedAngle);

                headingInput.InputTextBox.Text = selectedAngle.ToString("D3");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ground rotation failed: " + ex.GetType().Name + ": " + ex.Message, "Ground Rotation Error");
                return false;
            }
        }

        private void SaveHeadingItem_Click(object sender, EventArgs e)
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

                string aerodromeKey;
                if (!TryGetAerodromeKey(groundControl, out aerodromeKey))
                {
                    MessageBox.Show("Unable to determine the current aerodrome.", "Ground Rotation");
                    return;
                }

                int heading;
                if (!TryGetCurrentOrLastHeading(groundControl, out heading))
                {
                    MessageBox.Show("No heading is currently available to save.", "Ground Rotation");
                    return;
                }

                string existingLabel = rotationStore.GetSavedHeadingLabel(aerodromeKey, heading);
                string headingLabel;
                if (!TryPromptForOptionalHeadingLabel(ownerForm, heading, existingLabel, out headingLabel))
                {
                    return;
                }

                rotationStore.AddOrUpdateSavedHeading(aerodromeKey, heading, headingLabel);

                string message = "Saved heading " + heading.ToString("D3") + " for " + aerodromeKey + ".";
                if (!string.IsNullOrEmpty(headingLabel))
                {
                    message += " Label: " + headingLabel + ".";
                }
                MessageBox.Show(message, "Ground Rotation");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Save heading failed: " + ex.GetType().Name + ": " + ex.Message, "Ground Rotation Error");
            }
        }

        private void SetSavedHeadingAsAutoLoad_Click(object sender, EventArgs e)
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

                string aerodromeKey;
                if (!TryGetAerodromeKey(groundControl, out aerodromeKey))
                {
                    MessageBox.Show("Unable to determine the current aerodrome.", "Ground Rotation");
                    return;
                }

                object tag = clickedItem.Tag;
                if (!(tag is int))
                {
                    return;
                }
                int heading = (int)tag;

                rotationStore.SetAutoApplyHeading(aerodromeKey, heading);
                MessageBox.Show("Auto-apply set to " + heading.ToString("D3") + " for " + aerodromeKey + ".", "Ground Rotation");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Set auto-load failed: " + ex.GetType().Name + ": " + ex.Message, "Ground Rotation Error");
            }
        }

        private void ClearAutoApplyItem_Click(object sender, EventArgs e)
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

                string aerodromeKey;
                if (!TryGetAerodromeKey(groundControl, out aerodromeKey))
                {
                    MessageBox.Show("Unable to determine the current aerodrome.", "Ground Rotation");
                    return;
                }

                rotationStore.ClearAutoApplyHeading(aerodromeKey);
                MessageBox.Show("Cleared auto-apply heading for " + aerodromeKey + ".", "Ground Rotation");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Clear auto-apply failed: " + ex.GetType().Name + ": " + ex.Message, "Ground Rotation Error");
            }
        }

        private void LoadSavedHeading_Click(object sender, EventArgs e)
        {
            try
            {
                ToolStripMenuItem clickedItem = sender as ToolStripMenuItem;
                if (clickedItem == null)
                {
                    return;
                }

                object tag = clickedItem.Tag;
                if (!(tag is int))
                {
                    return;
                }

                int heading = (int)tag;
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

                lastAngleByGroundControl[groundControl] = heading;
                ApplyRotation(groundControl, heading);
                UpdateCheckedState(heading);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Load heading failed: " + ex.GetType().Name + ": " + ex.Message, "Ground Rotation Error");
            }
        }

        private void DeleteSavedHeading_Click(object sender, EventArgs e)
        {
            try
            {
                ToolStripMenuItem clickedItem = sender as ToolStripMenuItem;
                if (clickedItem == null)
                {
                    return;
                }

                object tag = clickedItem.Tag;
                if (!(tag is int))
                {
                    return;
                }

                int heading = (int)tag;
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

                string aerodromeKey;
                if (!TryGetAerodromeKey(groundControl, out aerodromeKey))
                {
                    MessageBox.Show("Unable to determine the current aerodrome.", "Ground Rotation");
                    return;
                }

                if (rotationStore.RemoveSavedHeading(aerodromeKey, heading))
                {
                    MessageBox.Show("Deleted heading " + heading.ToString("D3") + " for " + aerodromeKey + ".", "Ground Rotation");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Delete heading failed: " + ex.GetType().Name + ": " + ex.Message, "Ground Rotation Error");
            }
        }

        private bool TryPromptForOptionalHeadingLabel(IWin32Window owner, int heading, string existingLabel, out string headingLabel)
        {
            headingLabel = null;

            using (Form dialog = new Form())
            {
                dialog.Text = "Save Heading";
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(360, 140);

                Label promptLabel = new Label();
                promptLabel.AutoSize = false;
                promptLabel.Text = "Optional label for heading " + heading.ToString("D3") + ":";
                promptLabel.SetBounds(12, 12, 336, 20);

                TextBox labelInput = new TextBox();
                labelInput.SetBounds(12, 36, 336, 22);
                labelInput.Text = existingLabel ?? string.Empty;

                Label hintLabel = new Label();
                hintLabel.AutoSize = false;
                hintLabel.Text = "Leave empty to save without a label.";
                hintLabel.SetBounds(12, 62, 336, 18);

                Button okButton = new Button();
                okButton.Text = "Save";
                okButton.DialogResult = DialogResult.OK;
                okButton.SetBounds(192, 100, 75, 25);

                Button cancelButton = new Button();
                cancelButton.Text = "Cancel";
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.SetBounds(273, 100, 75, 25);

                dialog.Controls.AddRange(new Control[] { promptLabel, labelInput, hintLabel, okButton, cancelButton });
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;

                DialogResult result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                if (result != DialogResult.OK)
                {
                    return false;
                }

                string trimmed = (labelInput.Text ?? string.Empty).Trim();
                headingLabel = trimmed.Length == 0 ? null : trimmed;
                return true;
            }
        }

        private void PopulateSavedHeadingsMenu(ToolStripMenuItem rotationRoot, object groundControl)
        {
            ToolStripMenuItem savedHeadingsMenu = FindMenuItemByName(rotationRoot, SavedHeadingsMenuKey);
            if (savedHeadingsMenu == null || groundControl == null)
            {
                return;
            }

            savedHeadingsMenu.DropDownItems.Clear();

            string aerodromeKey;
            if (!TryGetAerodromeKey(groundControl, out aerodromeKey))
            {
                savedHeadingsMenu.Enabled = false;
                savedHeadingsMenu.ToolTipText = "No aerodrome selected.";
                return;
            }

            List<SavedHeadingInfo> headings = rotationStore.GetSavedHeadings(aerodromeKey);
            if (headings.Count == 0)
            {
                savedHeadingsMenu.Enabled = false;
                savedHeadingsMenu.DropDownItems.Add(new ToolStripMenuItem("No saved headings") { Enabled = false });
                return;
            }

            savedHeadingsMenu.Enabled = true;
            foreach (SavedHeadingInfo heading in headings)
            {
                string headingText = heading.Heading.ToString("D3");
                if (!string.IsNullOrEmpty(heading.Label))
                {
                    headingText += " [" + heading.Label + "]";
                }

                ToolStripMenuItem headingItem = new ToolStripMenuItem(headingText)
                {
                    TextAlign = ContentAlignment.MiddleLeft,
                    ShowShortcutKeys = false,
                    ShortcutKeyDisplayString = string.Empty
                };

                ToolStripDropDownMenu headingSubMenu = headingItem.DropDown as ToolStripDropDownMenu;
                if (headingSubMenu != null)
                {
                    headingSubMenu.ShowImageMargin = false;
                    headingSubMenu.ShowCheckMargin = false;
                }

                ToolStripMenuItem loadAction = new ToolStripMenuItem("Load")
                {
                    Tag = heading.Heading,
                    ShowShortcutKeys = false,
                    ShortcutKeyDisplayString = string.Empty
                };
                loadAction.Click += LoadSavedHeading_Click;

                ToolStripMenuItem deleteAction = new ToolStripMenuItem("Delete")
                {
                    Tag = heading.Heading,
                    ShowShortcutKeys = false,
                    ShortcutKeyDisplayString = string.Empty
                };
                deleteAction.Click += DeleteSavedHeading_Click;

                ToolStripMenuItem autoLoadAction = new ToolStripMenuItem("Set as Auto-Load")
                {
                    Tag = heading.Heading,
                    ShowShortcutKeys = false,
                    ShortcutKeyDisplayString = string.Empty
                };
                autoLoadAction.Click += SetSavedHeadingAsAutoLoad_Click;

                headingItem.DropDownItems.Add(loadAction);
                headingItem.DropDownItems.Add(deleteAction);
                headingItem.DropDownItems.Add(autoLoadAction);

                savedHeadingsMenu.DropDownItems.Add(headingItem);
            }
        }

        private void UpdateAutoApplyMenuState(ToolStripMenuItem rotationRoot, object groundControl)
        {
            ToolStripMenuItem clearMenu = FindMenuItemByName(rotationRoot, ClearAutoApplyMenuKey);
            if (clearMenu == null)
            {
                return;
            }

            string aerodromeKey;
            if (!TryGetAerodromeKey(groundControl, out aerodromeKey))
            {
                clearMenu.Enabled = false;
                return;
            }

            int? autoHeading = rotationStore.GetAutoApplyHeading(aerodromeKey);
            clearMenu.Enabled = autoHeading.HasValue;
            clearMenu.Text = autoHeading.HasValue
                ? "Disable Auto-Load (now " + autoHeading.Value.ToString("D3") + ")"
                : "Disable Auto-Load";
        }

        private ToolStripMenuItem FindMenuItemByName(ToolStripMenuItem root, string name)
        {
            if (root == null)
            {
                return null;
            }

            foreach (ToolStripItem item in root.DropDownItems)
            {
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null && string.Equals(menuItem.Name, name, StringComparison.Ordinal))
                {
                    return menuItem;
                }
            }

            return null;
        }

        private bool TryGetCurrentOrLastHeading(object groundControl, out int heading)
        {
            heading = 0;
            if (TryGetCurrentMagneticHeading(groundControl, out heading))
            {
                return true;
            }

            int lastHeading;
            if (lastAngleByGroundControl.TryGetValue(groundControl, out lastHeading))
            {
                heading = lastHeading;
                return true;
            }

            return false;
        }

        private bool TryGetAerodromeKey(object groundControl, out string aerodromeKey)
        {
            aerodromeKey = null;
            LogicalPositions.Position displayPosition;
            if (!TryGetDisplayPosition(groundControl, out displayPosition) || displayPosition == null)
            {
                return false;
            }

            aerodromeKey = TryGetAerodromeKeyFromPosition(displayPosition);
            if (string.IsNullOrEmpty(aerodromeKey))
            {
                return false;
            }

            aerodromeKey = aerodromeKey.Trim().ToUpperInvariant();
            return aerodromeKey.Length > 0;
        }

        private static string TryGetAerodromeKeyFromPosition(LogicalPositions.Position displayPosition)
        {
            if (displayPosition == null)
            {
                return null;
            }

            string[] candidateProperties =
            {
                "Aerodrome",
                "AerodromeICAO",
                "AerodromeName",
                "Icao",
                "ICAO",
                "Airport"
            };

            Type positionType = displayPosition.GetType();
            foreach (string propertyName in candidateProperties)
            {
                PropertyInfo property = positionType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null)
                {
                    continue;
                }

                object value;
                try
                {
                    value = property.GetValue(displayPosition, null);
                }
                catch
                {
                    continue;
                }

                string text = ExtractAerodromeText(value);
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }

            if (!string.IsNullOrEmpty(displayPosition.Name) && displayPosition.Name.Length >= 4)
            {
                return displayPosition.Name.Substring(0, 4);
            }

            return null;
        }

        private static string ExtractAerodromeText(object value)
        {
            if (value == null)
            {
                return null;
            }

            string asText = value as string;
            if (!string.IsNullOrEmpty(asText))
            {
                return asText;
            }

            Type valueType = value.GetType();
            string[] nestedNames = { "ICAO", "Icao", "Name" };
            foreach (string nestedName in nestedNames)
            {
                PropertyInfo property = valueType.GetProperty(nestedName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null)
                {
                    continue;
                }

                object nestedValue;
                try
                {
                    nestedValue = property.GetValue(value, null);
                }
                catch
                {
                    continue;
                }

                string nestedText = nestedValue as string;
                if (!string.IsNullOrEmpty(nestedText))
                {
                    return nestedText;
                }
            }

            return value.ToString();
        }

        private void HandleAutoApplyForGroundControl(object groundControl)
        {
            if (groundControl == null)
            {
                return;
            }

            string aerodromeKey;
            if (!TryGetAerodromeKey(groundControl, out aerodromeKey))
            {
                return;
            }

            string lastAerodrome;
            if (lastAerodromeByGroundControl.TryGetValue(groundControl, out lastAerodrome) && string.Equals(lastAerodrome, aerodromeKey, StringComparison.Ordinal))
            {
                return;
            }

            lastAerodromeByGroundControl[groundControl] = aerodromeKey;

            int? autoHeading = rotationStore.GetAutoApplyHeading(aerodromeKey);
            if (!autoHeading.HasValue)
            {
                return;
            }

            int heading = NormalizeHeading(autoHeading.Value);
            lastAngleByGroundControl[groundControl] = heading;
            ApplyRotation(groundControl, heading);
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
            Color applyLabelForeColor = GetEnabledMenuItemForeColor(rotationRoot, rotationRoot.DropDown.ForeColor);
            ToolStripTextBox referenceInput = FindReferenceTextBox(toolsMenu.DropDownItems, headingInput.InputTextBox);
            TextBox referenceControlInput = FindReferenceTextBoxControl(ownerForm, headingInput);
            headingInput.InputTextBox.Font = labelFont;

            if (referenceInput != null)
            {
                Color inputBackColor = referenceInput.TextBox == null ? referenceInput.BackColor : referenceInput.TextBox.BackColor;
                Color inputForeColor = referenceInput.TextBox == null ? referenceInput.ForeColor : referenceInput.TextBox.ForeColor;
                headingInput.ApplyStyle(inputBackColor, inputForeColor, ControlPaint.Dark(inputBackColor, 0.15f), labelFont);
                applyLabelForeColor = inputForeColor;
            }
            else if (referenceControlInput != null)
            {
                headingInput.ApplyStyle(referenceControlInput.BackColor, referenceControlInput.ForeColor, ControlPaint.Dark(referenceControlInput.BackColor, 0.15f), labelFont);
                applyLabelForeColor = referenceControlInput.ForeColor;
            }
            else
            {
                headingInput.ApplyStyle(rotationRoot.DropDown.BackColor, labelForeColor, Color.FromArgb(70, 70, 70), labelFont);
            }

            ApplyLabelHost applyLabelHost = GetApplyLabelHost(rotationRoot);
            if (applyLabelHost != null)
            {
                applyLabelHost.ApplyStyle(applyLabelForeColor, labelFont);
            }

            styledRotationInputs.Add(headingInput);
        }

        private ApplyLabelHost GetApplyLabelHost(ToolStripMenuItem rotationRoot)
        {
            if (rotationRoot == null)
            {
                return null;
            }

            foreach (ToolStripItem item in rotationRoot.DropDownItems)
            {
                ApplyLabelHost applyLabelHost = item as ApplyLabelHost;
                if (applyLabelHost != null)
                {
                    return applyLabelHost;
                }
            }

            return null;
        }

        private static Color GetEnabledMenuItemForeColor(ToolStripMenuItem rotationRoot, Color fallback)
        {
            if (rotationRoot == null)
            {
                return fallback;
            }

            foreach (ToolStripItem item in rotationRoot.DropDownItems)
            {
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem == null)
                {
                    continue;
                }

                if (menuItem.Enabled)
                {
                    return menuItem.ForeColor;
                }
            }

            return fallback;
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
            if (!int.TryParse((rawAngle ?? string.Empty).Trim(), out selectedAngle))
            {
                return false;
            }

            if (selectedAngle == 360)
            {
                selectedAngle = 0;
                return true;
            }

            return selectedAngle >= 0 && selectedAngle <= 359;
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

        private sealed class ApplyLabelHost : ToolStripControlHost
        {
            public ApplyLabelHost(HeadingInputHost headingInput)
                : base(new Label())
            {
                HeadingInput = headingInput;
                AutoSize = false;
                Size = new Size(120, 26);
                Margin = new Padding(0, 1, 6, 3);

                ApplyLabel.Text = "Apply";
                ApplyLabel.TextAlign = ContentAlignment.MiddleLeft;
                ApplyLabel.Cursor = Cursors.Hand;
                ApplyLabel.ForeColor = SystemColors.ControlText;
                ApplyLabel.Font = headingInput == null || headingInput.InputTextBox == null ? ApplyLabel.Font : headingInput.InputTextBox.Font;
                ApplyLabel.Tag = this;
                ApplyLabel.AutoSize = false;
                ApplyLabel.BackColor = Color.Transparent;
                ApplyLabel.Width = 112;
                ApplyLabel.Height = 22;
            }

            public void ApplyStyle(Color foreColor, Font font)
            {
                ApplyLabel.ForeColor = foreColor;
                if (font != null)
                {
                    ApplyLabel.Font = font;
                }
            }

            public HeadingInputHost HeadingInput
            {
                get;
                private set;
            }

            public Label ApplyLabel
            {
                get { return (Label)Control; }
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

        private sealed class SavedHeadingInfo
        {
            public int Heading;
            public string Label;
        }

        [DataContract]
        private sealed class RotationSettingsRoot
        {
            [DataMember(Order = 1)]
            public int SchemaVersion;

            [DataMember(Order = 2)]
            public Dictionary<string, RotationAerodromeSettings> Aerodromes;
        }

        [DataContract]
        private sealed class RotationAerodromeSettings
        {
            [DataMember(Order = 1)]
            public List<int> SavedHeadings;

            [DataMember(Order = 2)]
            public int? AutoApplyHeading;

            [DataMember(Order = 3)]
            public Dictionary<int, string> HeadingLabels;
        }

        private sealed class RotationSettingsStore
        {
            private readonly object sync = new object();
            private readonly string settingsPath;
            private RotationSettingsRoot settings;

            public RotationSettingsStore()
            {
                string basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "vatSys Rotation Plugin");
                settingsPath = Path.Combine(basePath, "rotations.json");
                settings = LoadSettings();
            }

            public List<SavedHeadingInfo> GetSavedHeadings(string aerodromeKey)
            {
                lock (sync)
                {
                    RotationAerodromeSettings entry;
                    if (!TryGetAerodromeEntry(aerodromeKey, false, out entry) || entry.SavedHeadings == null)
                    {
                        return new List<SavedHeadingInfo>();
                    }

                    List<int> sorted = new List<int>(entry.SavedHeadings);
                    sorted.Sort();

                    List<SavedHeadingInfo> result = new List<SavedHeadingInfo>();
                    foreach (int heading in sorted)
                    {
                        string label = null;
                        if (entry.HeadingLabels != null)
                        {
                            entry.HeadingLabels.TryGetValue(heading, out label);
                        }

                        result.Add(new SavedHeadingInfo
                        {
                            Heading = heading,
                            Label = string.IsNullOrEmpty(label) ? null : label
                        });
                    }

                    return result;
                }
            }

            public string GetSavedHeadingLabel(string aerodromeKey, int heading)
            {
                lock (sync)
                {
                    RotationAerodromeSettings entry;
                    if (!TryGetAerodromeEntry(aerodromeKey, false, out entry) || entry.HeadingLabels == null)
                    {
                        return null;
                    }

                    string label;
                    if (!entry.HeadingLabels.TryGetValue(NormalizeHeading(heading), out label))
                    {
                        return null;
                    }

                    return string.IsNullOrEmpty(label) ? null : label;
                }
            }

            public void AddOrUpdateSavedHeading(string aerodromeKey, int heading, string headingLabel)
            {
                lock (sync)
                {
                    RotationAerodromeSettings entry;
                    if (!TryGetAerodromeEntry(aerodromeKey, true, out entry))
                    {
                        return;
                    }

                    if (entry.SavedHeadings == null)
                    {
                        entry.SavedHeadings = new List<int>();
                    }

                    if (entry.HeadingLabels == null)
                    {
                        entry.HeadingLabels = new Dictionary<int, string>();
                    }

                    int normalized = NormalizeHeading(heading);
                    bool changed = false;

                    if (!entry.SavedHeadings.Contains(normalized))
                    {
                        entry.SavedHeadings.Add(normalized);
                        entry.SavedHeadings.Sort();
                        changed = true;
                    }

                    string trimmedLabel = string.IsNullOrEmpty(headingLabel) ? null : headingLabel.Trim();
                    if (string.IsNullOrEmpty(trimmedLabel))
                    {
                        if (entry.HeadingLabels.ContainsKey(normalized))
                        {
                            entry.HeadingLabels.Remove(normalized);
                            changed = true;
                        }
                    }
                    else
                    {
                        string existing;
                        if (!entry.HeadingLabels.TryGetValue(normalized, out existing) || !string.Equals(existing, trimmedLabel, StringComparison.Ordinal))
                        {
                            entry.HeadingLabels[normalized] = trimmedLabel;
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        SaveSettings();
                    }
                }
            }

            public bool RemoveSavedHeading(string aerodromeKey, int heading)
            {
                lock (sync)
                {
                    RotationAerodromeSettings entry;
                    if (!TryGetAerodromeEntry(aerodromeKey, false, out entry) || entry.SavedHeadings == null)
                    {
                        return false;
                    }

                    int normalized = NormalizeHeading(heading);
                    bool removed = entry.SavedHeadings.Remove(normalized);
                    if (!removed)
                    {
                        return false;
                    }

                    if (entry.HeadingLabels != null)
                    {
                        entry.HeadingLabels.Remove(normalized);
                    }

                    SaveSettings();
                    return true;
                }
            }

            public int? GetAutoApplyHeading(string aerodromeKey)
            {
                lock (sync)
                {
                    RotationAerodromeSettings entry;
                    if (!TryGetAerodromeEntry(aerodromeKey, false, out entry))
                    {
                        return null;
                    }

                    return entry.AutoApplyHeading;
                }
            }

            public void SetAutoApplyHeading(string aerodromeKey, int heading)
            {
                lock (sync)
                {
                    RotationAerodromeSettings entry;
                    if (!TryGetAerodromeEntry(aerodromeKey, true, out entry))
                    {
                        return;
                    }

                    entry.AutoApplyHeading = NormalizeHeading(heading);
                    SaveSettings();
                }
            }

            public void ClearAutoApplyHeading(string aerodromeKey)
            {
                lock (sync)
                {
                    RotationAerodromeSettings entry;
                    if (!TryGetAerodromeEntry(aerodromeKey, false, out entry))
                    {
                        return;
                    }

                    if (entry.AutoApplyHeading.HasValue)
                    {
                        entry.AutoApplyHeading = null;
                        SaveSettings();
                    }
                }
            }

            private bool TryGetAerodromeEntry(string aerodromeKey, bool createIfMissing, out RotationAerodromeSettings entry)
            {
                entry = null;
                if (string.IsNullOrEmpty(aerodromeKey))
                {
                    return false;
                }

                string key = aerodromeKey.Trim().ToUpperInvariant();
                if (key.Length == 0)
                {
                    return false;
                }

                EnsureSettingsInitialized();

                if (settings.Aerodromes == null)
                {
                    settings.Aerodromes = new Dictionary<string, RotationAerodromeSettings>(StringComparer.OrdinalIgnoreCase);
                }

                if (!settings.Aerodromes.TryGetValue(key, out entry) && createIfMissing)
                {
                    entry = new RotationAerodromeSettings
                    {
                        SavedHeadings = new List<int>(),
                        AutoApplyHeading = null,
                        HeadingLabels = new Dictionary<int, string>()
                    };
                    settings.Aerodromes[key] = entry;
                }

                if (entry != null && entry.HeadingLabels == null)
                {
                    entry.HeadingLabels = new Dictionary<int, string>();
                }

                return entry != null;
            }

            private void EnsureSettingsInitialized()
            {
                if (settings == null)
                {
                    settings = new RotationSettingsRoot();
                }

                if (settings.SchemaVersion <= 0)
                {
                    settings.SchemaVersion = 1;
                }

                if (settings.Aerodromes == null)
                {
                    settings.Aerodromes = new Dictionary<string, RotationAerodromeSettings>(StringComparer.OrdinalIgnoreCase);
                }
            }

            private RotationSettingsRoot LoadSettings()
            {
                try
                {
                    if (!File.Exists(settingsPath))
                    {
                        return CreateEmptySettings();
                    }

                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(RotationSettingsRoot));
                    using (FileStream stream = File.OpenRead(settingsPath))
                    {
                        RotationSettingsRoot loaded = serializer.ReadObject(stream) as RotationSettingsRoot;
                        if (loaded == null)
                        {
                            return CreateEmptySettings();
                        }

                        if (loaded.Aerodromes == null)
                        {
                            loaded.Aerodromes = new Dictionary<string, RotationAerodromeSettings>(StringComparer.OrdinalIgnoreCase);
                        }

                        if (loaded.SchemaVersion <= 0)
                        {
                            loaded.SchemaVersion = 1;
                        }

                        return loaded;
                    }
                }
                catch
                {
                    return CreateEmptySettings();
                }
            }

            private RotationSettingsRoot CreateEmptySettings()
            {
                return new RotationSettingsRoot
                {
                    SchemaVersion = 1,
                    Aerodromes = new Dictionary<string, RotationAerodromeSettings>(StringComparer.OrdinalIgnoreCase)
                };
            }

            private void SaveSettings()
            {
                EnsureSettingsInitialized();

                string directory = Path.GetDirectoryName(settingsPath);
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }

                Directory.CreateDirectory(directory);

                string tempPath = settingsPath + ".tmp";
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(RotationSettingsRoot));
                using (FileStream stream = File.Create(tempPath))
                {
                    serializer.WriteObject(stream, settings);
                }

                if (File.Exists(settingsPath))
                {
                    File.Delete(settingsPath);
                }
                File.Move(tempPath, settingsPath);
            }
        }

    }
}
