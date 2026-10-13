using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace GlbMerger
{
    // The mouth picked as five points on the painted mouth - both corners and three along the lip line
    // between them (MouthCurve). They place the mouth bones (FaceBoneRigger puts one on each), and
    // everything else about the mouth follows from them: the region's centre and width, and the open
    // mouth's lip line (Lip line Y, Corner Y, Lip width, fitted through them). The region's height
    // stays the panel's - it's how far the mouth's weights reach off the curve.
    //
    // Saved on the MouthAnchor's extras as "points", in the head's frame like the anchor itself.
    public partial class FaceBonesEditor
    {
        private const float MouthWidthPad = 1.15f;   // the region's half-width over the corners' half-span

        private readonly List<Vector3> _mouthPoints = new();   // mesh units; sorted by x once all five are in
        private bool _pickingMouthPoints;
        private Label _lblMouthPoints = null!;
        private Button _btnPickMouthPoints = null!, _btnClearMouthPoints = null!;

        private bool HasMouthPoints => _mouthPoints.Count == MouthCurve.PointCount && !_pickingMouthPoints;

        // ---- Extras ---------------------------------------------------------------------------

        private void ReadMouthPoints(JsonObject o)
        {
            _mouthPoints.Clear();
            if (o["points"] is not JsonArray arr || arr.Count != MouthCurve.PointCount) return;
            if (!Matrix4x4.Invert(_meshToHead, out var headToMesh)) return;
            foreach (var node in arr)
            {
                if (node is not JsonArray xyz || xyz.Count != 3) { _mouthPoints.Clear(); return; }
                var head = new Vector3((float)xyz[0]!.GetValue<double>(), (float)xyz[1]!.GetValue<double>(), (float)xyz[2]!.GetValue<double>());
                _mouthPoints.Add(Vector3.Transform(head, headToMesh));
            }
            SortMouthPoints();
        }

        private void WriteMouthPoints(JsonObject extras)
        {
            if (!HasMouthPoints) return;
            var arr = new JsonArray();
            foreach (var p in _mouthPoints)
            {
                var head = Vector3.Transform(p, _meshToHead);
                arr.Add(new JsonArray(Math.Round(head.X, 6), Math.Round(head.Y, 6), Math.Round(head.Z, 6)));
            }
            extras["points"] = arr;
        }

        private Vector3[]? MouthPointsForRig() => HasMouthPoints ? _mouthPoints.ToArray() : null;

        private string MouthPointsJson() =>
            "[" + string.Join(",", _mouthPoints.Select(p => $"[{FaceAnchors.Inv(p.X)},{FaceAnchors.Inv(p.Y)},{FaceAnchors.Inv(p.Z)}]")) + "]";

        // ---- Panel ----------------------------------------------------------------------------

        private void BuildMouthPointRows(FlowLayoutPanel flow)
        {
            var row = MakeRow();
            _btnPickMouthPoints = AddButton(row, "Pick Mouth Points", StartPickingMouthPoints);
            _btnClearMouthPoints = AddButton(row, "Clear Points", ClearMouthPoints);
            flow.Controls.Add(row);
            _help.Add(_btnPickMouthPoints,
                "Click five points on the painted mouth: both corners and three along the lip line between them, " +
                "in any order. A bone goes on each; the mouth region and the open mouth's lip line follow from " +
                "them. Drag a dot in the preview to adjust it.");
            _lblMouthPoints = Caption("", 10);
            flow.Controls.Add(_lblMouthPoints);
        }

        private void RefreshMouthPointUi()
        {
            bool mouth = Selected == Mouth;
            _btnPickMouthPoints.Enabled = mouth;
            _btnClearMouthPoints.Enabled = mouth && _mouthPoints.Count > 0;
            _lblMouthPoints.Text = _pickingMouthPoints
                ? $"Click the mouth: point {_mouthPoints.Count + 1} of {MouthCurve.PointCount}."
                : HasMouthPoints ? "Mouth points: set - drag the dots to adjust."
                                 : "Mouth points: none (bones placed from the region).";
            // The points place the mouth region and its lip line, so those aren't hand-set meanwhile.
            bool placed = mouth && HasMouthPoints;
            foreach (var n in new[] { _numX, _numY, _numZ, _numW }) n.Enabled &= !placed;
            SetLipLineEditable(!HasMouthPoints);
        }

        private void StartPickingMouthPoints()
        {
            _mouthPoints.Clear();
            _pickingMouthPoints = true;
            RefreshUiFromState();
            PushState();
        }

        private void ClearMouthPoints()
        {
            _mouthPoints.Clear();
            _pickingMouthPoints = false;
            _dirty = true;
            RefreshUiFromState();
            PushState();
        }

        // ---- From the preview -------------------------------------------------------------------

        private void OnMouthPointClicked(Vector3 p)
        {
            if (!_pickingMouthPoints) return;
            _mouthPoints.Add(p);
            if (_mouthPoints.Count == MouthCurve.PointCount)
            {
                _pickingMouthPoints = false;
                SortMouthPoints();
                ApplyMouthPoints();
            }
            RefreshUiFromState();
            PushState();
        }

        private void OnMouthPointMoved(int i, Vector3 p)
        {
            if (i < 0 || i >= _mouthPoints.Count) return;
            _mouthPoints[i] = p;
            if (HasMouthPoints) { SortMouthPoints(); ApplyMouthPoints(); }
            RefreshUiFromState();
            PushState();
        }

        private void SortMouthPoints()
        {
            var sorted = _mouthPoints.OrderBy(p => p.X).ToList();
            _mouthPoints.Clear();
            _mouthPoints.AddRange(sorted);
        }

        // ---- What the points decide ----------------------------------------------------------

        // The mouth region round the points (centre between the corners, at the points' mean height and
        // depth; wide enough to take both corners), then the lip line fitted through them.
        private void ApplyMouthPoints()
        {
            if (!HasMouthPoints) return;
            var curve = new MouthCurve(_mouthPoints.ToArray());
            var region = _regions[Mouth];
            if (!region.Set) region = new AnchorRegion { RadiiPct = DefaultMouthPct, FrontPct = DefaultMouthPct.Z };
            region.Set = true;
            region.Centre = new Vector3(curve.MidX, _mouthPoints.Average(p => p.Y), _mouthPoints.Average(p => p.Z));
            region.RadiiPct = new Vector3(curve.HalfSpan * MouthWidthPad / _modelHeight * 100f, region.RadiiPct.Y, region.RadiiPct.Z);
            _regions[Mouth] = region;
            FitLipLineToPoints();
            _dirty = true;
        }

        // Lip line Y and Corner Y as the least-squares parabola y = a + b t^2 through the points (t = -1..1
        // corner to corner), in region units; Lip width out to the corners.
        private void FitLipLineToPoints()
        {
            if (!HasMouthPoints || !_regions[Mouth].Set) return;
            var region = _regions[Mouth];
            float ry = Math.Max(region.RadiiPct.Y * _modelHeight / 100f, 1e-6f);
            var curve = new MouthCurve(_mouthPoints.ToArray());

            double n = 0, st2 = 0, st4 = 0, sy = 0, syt2 = 0;
            for (int k = 0; k < MouthCurve.PointCount; k++)
            {
                double t2 = curve.Across(k) * curve.Across(k), y = (curve[k].Y - region.Centre.Y) / ry;
                n += 1; st2 += t2; st4 += t2 * t2; sy += y; syt2 += y * t2;
            }
            double det = n * st4 - st2 * st2;
            double b = Math.Abs(det) > 1e-9 ? (n * syt2 - st2 * sy) / det : 0;
            double a = (sy - b * st2) / n;

            _mouth.LipY = (float)a;
            _mouth.LipCornerY = (float)(a + b);
            _mouth.LipWidthPct = 100f / MouthWidthPad;
        }
    }
}
