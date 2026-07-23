using System;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Forms;

namespace GlbMerger
{
    // A DataGridView column whose cells edit via a NumericUpDown control - gives every row its
    // own small up/down spinner buttons for nudging a value by Increment, instead of only being
    // able to type a replacement value.
    public class NumericUpDownColumn : DataGridViewColumn
    {
        public decimal Minimum { get; set; }
        public decimal Maximum { get; set; } = 100m;
        public decimal Increment { get; set; } = 1m;
        public int DecimalPlaces { get; set; }

        // When true, a value that goes past Maximum (typed directly, or via the up/down buttons)
        // wraps back around to Minimum instead of clamping at the boundary - e.g. typing 400 for
        // a 0-359.99 degree angle becomes 40, not 359.99.
        public bool WrapAround { get; set; }

        public NumericUpDownColumn() : base(new NumericUpDownCell())
        {
        }

        public override DataGridViewCell? CellTemplate
        {
            get => base.CellTemplate;
            set
            {
                if (value != null && value is not NumericUpDownCell)
                    throw new InvalidCastException("CellTemplate must be a NumericUpDownCell");
                base.CellTemplate = value;
            }
        }
    }

    public class NumericUpDownCell : DataGridViewTextBoxCell
    {
        public override Type EditType => typeof(NumericUpDownEditingControl);
        public override Type ValueType => typeof(decimal);
        public override object DefaultNewRowValue => 0m;

        public override void InitializeEditingControl(int rowIndex, object? initialFormattedValue, DataGridViewCellStyle dataGridViewCellStyle)
        {
            base.InitializeEditingControl(rowIndex, initialFormattedValue, dataGridViewCellStyle);

            if (DataGridView?.EditingControl is not NumericUpDownEditingControl control) return;
            if (OwningColumn is not NumericUpDownColumn column) return;

            control.Minimum = column.Minimum;
            control.Maximum = column.Maximum;
            control.Increment = column.Increment;
            control.DecimalPlaces = column.DecimalPlaces;
            control.WrapAround = column.WrapAround;
            control.Value = decimal.TryParse(Value?.ToString(), out var v) ? Math.Clamp(v, column.Minimum, column.Maximum) : 0m;
        }
    }

    public class NumericUpDownEditingControl : NumericUpDown, IDataGridViewEditingControl
    {
        private DataGridView? _dataGridView;
        private bool _valueChanged;

        public bool WrapAround { get; set; }

        public DataGridView? EditingControlDataGridView { get => _dataGridView; set => _dataGridView = value; }
        [AllowNull]
        public object EditingControlFormattedValue
        {
            get => Value.ToString();
            set { if (decimal.TryParse(value?.ToString(), out var v)) Value = Math.Clamp(v, Minimum, Maximum); }
        }
        public int EditingControlRowIndex { get; set; }
        public bool EditingControlValueChanged { get => _valueChanged; set => _valueChanged = value; }
        public Cursor EditingPanelCursor => base.Cursor;
        public bool RepositionEditingControlOnValueChange => false;

        public object GetEditingControlFormattedValue(DataGridViewDataErrorContexts context) => EditingControlFormattedValue;
        public void ApplyCellStyleToEditingControl(DataGridViewCellStyle dataGridViewCellStyle)
        {
            Font = dataGridViewCellStyle.Font;
            ForeColor = dataGridViewCellStyle.ForeColor;
            BackColor = dataGridViewCellStyle.BackColor;
        }
        public bool EditingControlWantsInputKey(Keys key, bool dataGridViewWantsInputKey) =>
            key is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End;
        public void PrepareEditingControlForEdit(bool selectAll) { }

        protected override void OnValueChanged(EventArgs e)
        {
            base.OnValueChanged(e);
            _valueChanged = true;
            _dataGridView?.NotifyCurrentCellDirty(true);
        }

        // Wraps a value that's out of [Minimum, Maximum] back into range (400 -> 40 for a
        // 0-359.99 range) instead of NumericUpDown's default clamp-at-the-boundary behavior -
        // only when WrapAround is set (used for the Y-rotation column, not the Y-offset one).
        private decimal Wrap(decimal value)
        {
            var range = Maximum - Minimum;
            if (range <= 0) return value;
            var wrapped = (value - Minimum) % range;
            if (wrapped < 0) wrapped += range;
            return Minimum + wrapped;
        }

        protected override void ValidateEditText()
        {
            if (WrapAround && decimal.TryParse(Text, out var raw) && (raw < Minimum || raw > Maximum))
            {
                Value = Wrap(raw);
                Text = Value.ToString();
                return;
            }
            base.ValidateEditText();
        }

        public override void UpButton()
        {
            if (WrapAround && Value + Increment > Maximum) Value = Wrap(Value + Increment);
            else base.UpButton();
        }

        public override void DownButton()
        {
            if (WrapAround && Value - Increment < Minimum) Value = Wrap(Value - Increment);
            else base.DownButton();
        }
    }
}
