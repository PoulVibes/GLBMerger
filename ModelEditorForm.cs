using System;
using System.Windows.Forms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // The single post-merge editing window. Everything that used to be its own dialog (Fix Joint
    // Orientation, Ball Anchor, Stiff Arm Poses) plus the old standalone model viewer's animation
    // trimming is now a mode of this one form, picked from the dropdown at the top - so there's
    // one window, one "Done", and one place the left-hand controls live, instead of four windows
    // each re-teaching the user where its own controls are.
    //
    // Every mode edits the SAME in-memory ModelRoot in place (that's the pattern all four already
    // used as dialogs), so nothing needs committing when the mode changes or when this closes -
    // whatever the modes wrote is what MainForm's Save button writes out.
    public class ModelEditorForm : Form
    {
        public enum EditorMode
        {
            JointOrientation,
            Rigging,
            BallAnchor,
            StiffArm,
            Handoff,
            AnimationTrim,
            OptimizeGeometry,
            ModelSmoother,
            ModelAdjuster,
            TextureEditor,
            RigidRegion,
            FlattenModel,
        }

        // Dropdown order and labels, indexed by EditorMode.
        private static readonly (EditorMode Mode, string Label)[] Modes =
        {
            (EditorMode.JointOrientation, "Fix Joint Orientation"),
            (EditorMode.Rigging,          "Rigging (Move Joints)"),
            (EditorMode.BallAnchor,       "Ball Anchor"),
            (EditorMode.StiffArm,         "Stiff Arm Poses"),
            (EditorMode.Handoff,          "Ball Handoff (View Only)"),
            (EditorMode.AnimationTrim,    "Animation Start / Stop (Trim)"),
            (EditorMode.OptimizeGeometry, "Optimize Geometry"),
            (EditorMode.ModelSmoother,    "Model Smoother"),
            (EditorMode.ModelAdjuster,    "Model Adjuster"),
            (EditorMode.TextureEditor,    "Texture Editor"),
            (EditorMode.RigidRegion,      "Rigid Region (Prevent Skew)"),
            (EditorMode.FlattenModel,     "Flatten Model (Billboards)"),
        };

        private readonly ModelRoot _model;
        private readonly bool _darkMode;
        private readonly AppSettings _settings;

        private ComboBox _modeDropdown = null!;
        private Panel _host = null!;
        private Control? _currentEditor;
        private int _currentModeIndex = -1;
        private bool _revertingMode;

        public ModelEditorForm(ModelRoot model, bool darkMode = false, AppSettings? settings = null)
        {
            _model = model;
            _darkMode = darkMode;
            _settings = settings ?? new AppSettings();

            Text = "Model Editor";
            Width = 1060;
            Height = 880;
            MinimumSize = new System.Drawing.Size(780, 620);
            StartPosition = FormStartPosition.CenterParent;

            BuildUi();

            ThemeManager.Apply(this, darkMode);

            // Deferred to Shown rather than run from the constructor: each editor spins up a
            // WebView2 the moment it's created, and doing that before this form has a window
            // handle of its own leaves the browser control sized against a not-yet-laid-out
            // parent. By Shown the host panel has its real size.
            // Reopens on whichever editor was used last, by name so reordering the list is harmless.
            Shown += (s, e) =>
            {
                int last = Array.FindIndex(Modes, m => m.Mode.ToString() == _settings.LastEditorMode);
                _modeDropdown.SelectedIndex = Math.Max(last, 0);
            };

            // Every mode edits the model in place, but some stage their edits until an Apply /
            // Save / Bake - closing the window throws those away, so ask first.
            FormClosing += (s, e) =>
            {
                if (!ConfirmDiscard("close the editor")) e.Cancel = true;
            };
        }

        // True when there's nothing unapplied in the current editor, or the user agrees to lose it.
        private bool ConfirmDiscard(string action)
        {
            if (_currentEditor is not IUnappliedChanges editor || !editor.HasUnappliedChanges) return true;
            string mode = _currentModeIndex >= 0 ? Modes[_currentModeIndex].Label : "This editor";
            return MessageBox.Show(this,
                $"{mode} has {editor.UnappliedChangesDescription} that haven't been applied to the model.\n\n" +
                $"Discard them and {action}?",
                "Unapplied changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
                == DialogResult.Yes;
        }

        private void BuildUi()
        {
            // Column 3 takes all the slack, which is what pins "Done" to the right edge without
            // it needing to track the form's width itself.
            var topBar = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                RowCount = 1,
                Padding = new Padding(10, 8, 10, 8),
            };
            topBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            topBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var lblMode = new Label { Text = "Editor:", AutoSize = true, Margin = new Padding(3, 8, 8, 3) };

            _modeDropdown = new ComboBox { Width = 240, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 4, 3, 3) };
            foreach (var (_, label) in Modes) _modeDropdown.Items.Add(label);
            _modeDropdown.SelectedIndexChanged += (s, e) =>
            {
                if (_revertingMode || _modeDropdown.SelectedIndex == _currentModeIndex) return;
                // Switching disposes the current editor, which loses its unapplied edits too.
                if (!ConfirmDiscard("switch editors"))
                {
                    _revertingMode = true;
                    try { _modeDropdown.SelectedIndex = _currentModeIndex; }
                    finally { _revertingMode = false; }
                    return;
                }
                _currentModeIndex = _modeDropdown.SelectedIndex;
                _settings.LastEditorMode = Modes[_currentModeIndex].Mode.ToString();
                ShowEditor(Modes[_currentModeIndex].Mode);
            };

            var btnClose = new Button
            {
                Text = "Done",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new System.Drawing.Size(90, 0),
                Margin = new Padding(3, 4, 3, 3),
                Anchor = AnchorStyles.Right,
                DialogResult = DialogResult.OK,
            };

            topBar.Controls.Add(lblMode, 0, 0);
            topBar.Controls.Add(_modeDropdown, 1, 0);
            topBar.Controls.Add(new Panel { Width = 1, Height = 1, Margin = new Padding(0) }, 2, 0);
            topBar.Controls.Add(btnClose, 3, 0);
            AcceptButton = btnClose;

            _host = new Panel { Dock = DockStyle.Fill };

            Controls.Add(_host);
            Controls.Add(topBar);
        }

        // The outgoing editor is disposed and the incoming one built from scratch, rather than
        // both being kept alive and just shown/hidden. Each editor snapshots the model to its own
        // preview file when it starts up, so a cached one would still be showing (and dialing its
        // sliders against) the model as it was before whatever another mode has since baked into
        // it. Rebuilding is the same guarantee these editors had as dialogs, where each one was
        // constructed fresh every time it was opened.
        private void ShowEditor(EditorMode mode)
        {
            Cursor = Cursors.WaitCursor;
            _host.SuspendLayout();
            try
            {
                var previous = _currentEditor;
                _currentEditor = null;
                if (previous != null)
                {
                    _host.Controls.Remove(previous);
                    previous.Dispose();
                }

                Control editor = mode switch
                {
                    EditorMode.JointOrientation => new JointOrientationEditor(_model, _darkMode),
                    EditorMode.Rigging => new RiggingEditor(_model, _darkMode, _settings),
                    EditorMode.BallAnchor => new BallAnchorEditor(_model, _darkMode, _settings),
                    EditorMode.StiffArm => new StiffArmPoseEditor(_model, _darkMode, _settings),
                    EditorMode.Handoff => new HandoffViewer(_model, _darkMode),
                    EditorMode.AnimationTrim => new AnimationTrimEditor(_model, _darkMode,
                        _settings.AnimationTrimPlaybackSpeed, speed => _settings.AnimationTrimPlaybackSpeed = speed),
                    EditorMode.OptimizeGeometry => new GeometryOptimizerEditor(_model, _darkMode),
                    EditorMode.ModelSmoother => new ModelSmootherEditor(_model, _darkMode),
                    EditorMode.ModelAdjuster => new ModelAdjusterEditor(_model, _darkMode),
                    EditorMode.TextureEditor => new TextureEditorEditor(_model, _darkMode),
                    EditorMode.RigidRegion => new RigidRegionEditor(_model, _darkMode),
                    EditorMode.FlattenModel => new ModelFlattenerEditor(_model, _darkMode, _settings),
                    _ => throw new ArgumentOutOfRangeException(nameof(mode)),
                };

                _host.Controls.Add(editor);
                _currentEditor = editor;
            }
            finally
            {
                _host.ResumeLayout(performLayout: true);
                Cursor = Cursors.Default;
            }
        }
    }
}
