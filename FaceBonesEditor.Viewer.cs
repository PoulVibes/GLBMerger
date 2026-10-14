namespace GlbMerger
{
    // FaceBonesEditor's script for the shared preview page (FacePreviewPage): the regions tinted over
    // the model, the saved face weights tinted per vertex, and the expression presets played on the
    // face bones with a copy of the game's maths (FaceBones::MotionFor and its head-frame move, the
    // presets from FaceExpression.cpp) - keep them in step, or the preview stops being what the game
    // does. Bones are found by their "face" extras, which GLTFLoader hands over as userData. The open
    // mouth is a copy of the game's shaders/MouthOverlay.glsl, in the mouth region's frame
    // (MouthOverlay's is body-aligned; this one's the mesh axes - the same for an upright model).
    public partial class FaceBonesEditor
    {
        private const string ViewerScript = @"
        // ---- Region and weight tint ----
        var VERT_HEAD = [
            'uniform vec4 faceBoneIdx[2];',   // skeleton indices of the face bones, -1 = none
            'varying vec3 vBindPos;',
            'varying float vFaceW;', ''
        ].join('\n');
        var VERT_BODY = [
            '',
            'vBindPos = position;',
            'vFaceW = 0.0;',
            '#ifdef USE_SKINNING',
            'for (int k = 0; k < 4; ++k) {',
            '    for (int f = 0; f < 8; ++f) {',
            '        float fi = faceBoneIdx[f / 4][f - (f / 4) * 4];',
            '        if (fi >= 0.0 && abs(skinIndex[k] - fi) < 0.5) vFaceW += skinWeight[k];',
            '    }',
            '}',
            '#endif', ''
        ].join('\n');
        // shaders/MouthOverlay.glsl's MouthPaint, verbatim (the game file's marked section).
        var MOUTH_CORE = `
// ---- MouthPaint: copied verbatim into GLBMerger's FaceBonesEditor.Viewer.cs, keep in step ----
struct MouthParams
{
    float open, edge;
    vec4  lip;                          // lipY, lipCornerY, lipHalfWidth, openDepth
    vec3  interior; float teethUpper;   // teeth: how far each row shows, 0..1
    vec3  teeth;    float teethLower;
    vec3  tongue;   float upperTooth;   // the front upper teeth's length past the lip
    vec3  lips;     float lipThickness;
    vec3  outline;  float lowerTooth;   // the front lower teeth's length
    vec3  skin;     float maskMargin;   // mask margin 0 = no mask
    float teethWidth;                   // the teeth arch's half-width, share of the lip's
    float round;                        // 0..1: narrower, the upper lip lifted -- oh, oo
    float tongueUp;                     // 0..1: the tongue's tip up behind the teeth -- t, l, th
    float bite;                         // -1 underbite .. 0 even .. +1 overbite
    float toothWidth;                   // x the standard tooth's width
    float toothGap;                     // 0..1 of a tooth between each pair
    float creaseLength, creaseWidth;    // the corner creases (length 0 = none)
    float creaseCurve;                  // 0 straight .. 1 a half circle
    float creaseAngle;                  // radians the crease's middle turns up from straight out
    float creaseOffset;                 // how far outside the opening's corner
    vec3  creaseColor;
    float baseOpen;                     // how open the painted mouth is; > 0: lips drawn round the
                                        // opening, there even shut, so it can close over the paint
    float lipOpacity;                   // 0..1: how much the lips cover the paint under them
    float lipArc;                       // the bottom lip's curve: 1 round, below flatter, above deeper in the middle
    float alwaysDrawn;                  // 1: the drawn mouth never hands back to the paint, even at rest
};

// 1 where d < 0, antialiased over aa.
float MouthIn(float d, float aa) { return 1.0 - smoothstep(-aa, aa, d); }

// How far the drawn mouth has taken over from the painted one: none while the
// mouth is as painted (shut, or a painted-open mouth at its painting), fully
// once it has moved 3% from it -- opening a painted-shut mouth, or opening or
// shutting a painted-open one -- or always, for a mouth set always drawn (then
// with lips that meet on a seam when shut, as a painted-open mouth's).
float MouthSwap(MouthParams p)
{
    return max(smoothstep(0.0, 0.03, abs(p.open - p.baseOpen)), step(0.5, p.alwaysDrawn));
}

// Where the opening is at feature point f (region units): the painted lip
// line runs hl either side; the opening along it is narrower when rounded,
// and its upper lip lifts as the lower one drops.
struct MouthShape
{
    float hl, hw, arc, depth, lift, line, top, bottom, dOuter;
    float lipsShut;                     // a baked-open mouth's lips, outside the opening (0 otherwise)
};

MouthShape MouthShapeAt(vec2 f, MouthParams p)
{
    MouthShape s;
    s.hl     = max(p.lip.z, 1e-4);
    s.hw     = s.hl * (1.0 - 0.55 * p.round);
    float t  = clamp(f.x / s.hw, -1.0, 1.0);
    float tl = clamp(f.x / s.hl, -1.0, 1.0);
    s.arc    = sqrt(max(1.0 - t * t, 0.0));          // 1 in the middle, 0 at the opening's corners
    s.depth  = p.open * p.lip.w;
    s.lift   = p.round * 0.35 * s.depth;
    s.line   = mix(p.lip.x, p.lip.y, tl * tl);       // the painted lip line
    s.top    = s.line + s.lift * s.arc;
    s.lipsShut = p.baseOpen > 0.0 || p.alwaysDrawn > 0.5 ? p.lipThickness * (1.0 + 0.5 * p.round) : 0.0;
    s.bottom = s.top - s.depth * pow(s.arc, max(p.lipArc, 0.05)) - 2.0 * s.lipsShut * sqrt(s.arc);
    s.dOuter = max(max(f.y - s.top, s.bottom - f.y), abs(f.x) - s.hw);
    return s;
}

// A row of teeth on an arch of half-width R seen from the front: equal turns
// round the arch, so they narrow and darken toward the sides, square tips with
// rounded corners, a gap (gap, share of a tooth) and a thin shadow between
// each. edge is how far the point is from the row's base toward its tips; len
// the middle teeth's length. Two incisors meet on the midline. Returns
// (shade, coverage).
vec2 MouthTeethRow(float x, float edge, float len, float R, float turn, float gap, float aa)
{
    float sx   = clamp(x / R, -0.999, 0.999);
    float u    = asin(sx);                           // angle round the arch
    float side = abs(u) / 1.5708;
    float du   = aa / max(R * sqrt(1.0 - sx * sx), 1e-4);
    float s    = fract(u / turn);                    // across this tooth
    float near = min(s, 1.0 - s) * turn;             // angle to the nearest neighbour
    float tip  = len * (1.0 - 0.35 * side * side) * (1.0 - 0.22 * pow(abs(2.0 * s - 1.0), 6.0));
    float cover = MouthIn(edge - tip, aa) * MouthIn(abs(x) - R, aa)
                * smoothstep(0.5 * gap * turn, 0.5 * gap * turn + du, near)
                * (1.0 - smoothstep(0.75, 1.0, side));   // back teeth lost in the dark
    float line  = 1.0 - smoothstep(0.5 * gap * turn, 0.5 * gap * turn + 1.5 * du, near);
    float shade = mix(1.0, 0.3, pow(side, 1.5)) * (1.0 - 0.45 * line)
                * mix(1.0, 0.85, smoothstep(0.6, 1.0, edge / max(tip, 1e-4)));   // tips a touch translucent
    return vec2(shade, cover);
}

// Colour + coverage of the open mouth at feature point f.
vec4 MouthPaint(vec2 f, float aa, MouthParams p, MouthShape g)
{
    // The opening, and the outline round it: edge wide, outside the opening.
    float open  = MouthIn(g.dOuter, aa);
    float ring  = MouthIn(g.dOuter - p.edge, aa) * step(1e-4, p.edge);
    if (max(open, ring) <= 0.0) return vec4(0.0);

    // Lips: a band inside the outline, the lower one fuller, both thinning to
    // the corners and never taking more than half the opening -- or, round a
    // baked-open mouth, their full thickness outside it, there even shut.
    float lipMid = g.lipsShut > 0.0 ? g.lipsShut
                                    : min(p.lipThickness * (1.0 + 0.5 * p.round), g.depth * 0.25);   // pursed: fuller
    float lipT   = lipMid * sqrt(g.arc);
    float upperT = lipT * 0.8, lowerT = lipT * 1.2;
    float inTop  = g.top - upperT, inBottom = g.bottom + lowerT;
    float inner  = MouthIn(max(max(f.y - inTop, inBottom - f.y), abs(f.x) - g.hw), aa);

    // The mouth behind: dark, darkest up under the palate.
    float shut      = g.lipsShut * 2.0;                            // lips added round a baked-open mouth
    float ih        = max(g.depth + shut - lipMid * 2.0, 1e-4);   // the inside's height in the middle
    float midBottom = p.lip.x + g.lift - g.depth - shut + lipMid * 1.2;   // and its bottom there
    vec3  c  = p.interior * mix(1.0, 0.6, clamp((f.y - midBottom) / ih, 0.0, 1.0));

    // Tongue: a low mound across most of the opening, its top dipping at the
    // midline groove, lighter on its crown -- or, tip up, a narrower one
    // reaching for the upper teeth.
    float tw     = g.hw * mix(0.72, 0.45, p.tongueUp);
    float tx     = clamp(f.x / tw, -1.0, 1.0);
    float crown  = midBottom + ih * mix(0.55, 0.95, p.tongueUp) * sqrt(max(1.0 - tx * tx, 0.0));
    float groove = ih * 0.07 * (1.0 - p.tongueUp) * exp(-pow(f.x / (tw * 0.16), 2.0));
    float dTop   = f.y - (crown - groove);
    float tongue = MouthIn(dTop, aa) * MouthIn(abs(f.x) - tw, aa);
    float tShade = mix(0.7, 1.05, sqrt(max(1.0 - tx * tx, 0.0)))           // rounding off at the sides
                 * mix(1.0, 0.82, exp(-pow(f.x / (tw * 0.05), 2.0)))       // the groove's line
                 * mix(1.0, 0.85, smoothstep(0.0, ih * 0.3, -dTop));       // lit on the crown
    c = mix(c, p.tongue * tShade, tongue);

    // Teeth: the lower row up from behind the bottom lip, the upper row down
    // from behind the top one. How much of each shows is how far it reaches
    // past its lip, not how solid it is. An overbite lengthens the upper row
    // and draws it in front; an underbite the lower, wider and in front.
    float R     = g.hl * p.teethWidth;
    float over  = max(p.bite, 0.0), under = max(-p.bite, 0.0);
    float tw0   = 0.3 * p.toothWidth;
    vec2 lower = MouthTeethRow(f.x, f.y - inBottom, p.lowerTooth * p.teethLower * (1.0 + 0.4 * under),
                               R * mix(0.85, 1.0, under), tw0 * 0.8, p.toothGap, aa);
    vec2 upper = MouthTeethRow(f.x, inTop - f.y, p.upperTooth * p.teethUpper * (1.0 + 0.4 * over),
                               R, tw0, p.toothGap, aa);
    vec3 lowerC = p.teeth * lower.x;
    vec3 upperC = p.teeth * upper.x * mix(0.8, 1.0, smoothstep(0.0, p.upperTooth * 0.3, inTop - f.y));
    if (p.bite >= 0.0) { c = mix(c, lowerC, lower.y); c = mix(c, upperC, upper.y); }
    else               { c = mix(c, upperC, upper.y); c = mix(c, lowerC, lower.y); }

    // Lips over it all: shaded darker into the mouth, the lower one catching
    // a highlight across its fullest part.
    float lv   = clamp((f.y - g.bottom) / max(lowerT, 1e-4), 0.0, 1.0);   // 0 outer edge .. 1 inner, lower lip
    float uv   = clamp((g.top - f.y) / max(upperT, 1e-4), 0.0, 1.0);      // the same, upper lip
    float isUp = step(0.5 * (g.top + g.bottom), f.y);
    float l    = mix(lv, uv, isUp);
    vec3  lipC = p.lips * mix(1.0, 0.7, l * l) * mix(1.0, 0.85, isUp)
               + p.lips * 0.35 * exp(-pow((lv - 0.45) / 0.22, 2.0)) * (1.0 - isUp);
    c = mix(lipC, c, inner);

    // A baked-open mouth closing: where its lips meet, a seam in the outline colour.
    float seam = MouthIn(abs(f.y - 0.5 * (inTop + inBottom)) - max(0.5 * p.edge, aa), aa)
               * MouthIn(abs(f.x) - g.hw, aa) * (1.0 - smoothstep(0.0, 0.15, g.depth)) * step(1e-4, g.lipsShut);
    c = mix(c, p.outline, seam);

    // The outline round the outside: the opening keeps its full size, the
    // line grows outward from it. The lips cover the paint lipOpacity of the
    // way; the inside of the mouth and the seam fully.
    float alpha = max(mix(p.lipOpacity, 1.0, max(inner, seam)) * open, ring * 0.85);
    c = mix(p.outline, c, open);
    return vec4(c, alpha * MouthSwap(p));
}

// Whenever the drawn mouth is in (MouthSwap), the painted one is covered in skin, so
// none of it shows round the drawn one -- a baked-on smile past an O shape's
// corners, painted lips below the opening: everything within maskMargin of the
// painted lip line (its whole width) or of the opening, solid skin colour.
float MouthMaskZone(vec2 f, float aa, MouthParams p, MouthShape g)
{
    if (p.maskMargin <= 0.0) return 0.0;
    float dLine = length(vec2(max(abs(f.x) - g.hl, 0.0), f.y - g.line));
    float d     = min(dLine, max(g.dOuter, 0.0));
    // A painted-open mouth: its painted opening too (the drawn one at baseOpen
    // is lined up with it), however far the drawn one has shut.
    if (p.baseOpen > 0.0)
    {
        MouthParams painted = p;
        painted.open  = p.baseOpen;
        painted.round = 0.0;
        d = min(d, max(MouthShapeAt(f, painted).dOuter, 0.0));
    }
    return MouthIn(d - p.maskMargin, aa) * MouthSwap(p);
}

vec3 MouthMask(vec3 albedo, vec2 f, float aa, MouthParams p, MouthShape g)
{
    return mix(albedo, p.skin, MouthMaskZone(f, aa, p, g));
}
// A crease at each corner of the opening while it's open, like the ones a
// smile leaves: a short arc just outside the corner, bulging away from the
// mouth (a closing bracket at the right corner), its middle tilted up by creaseAngle,
// tapering to points at both ends. Mirrored left to right.
vec3 MouthCreases(vec3 albedo, vec2 f, float aa, MouthParams p, MouthShape g)
{
    if (p.creaseLength <= 0.0 || p.creaseWidth <= 0.0) return albedo;
    vec2  q    = vec2(abs(f.x), f.y);
    float tc   = min(g.hw / g.hl, 1.0);
    vec2  c    = vec2(g.hw + p.creaseOffset, mix(p.lip.x, p.lip.y, tc * tc));   // the arc's middle
    float span = max(p.creaseCurve, 1e-3) * 3.14159;                           // the angle it turns through
    float R    = p.creaseLength / span;
    vec2  o    = c - R * vec2(cos(p.creaseAngle), sin(p.creaseAngle));          // its centre, toward the mouth
    vec2  d    = q - o;
    float ang  = mod(atan(d.y, d.x) - p.creaseAngle + 3.14159, 6.28318) - 3.14159;
    float s    = clamp(ang / (0.5 * span), -1.0, 1.0);                          // along it, end to end
    float at   = p.creaseAngle + s * 0.5 * span;
    float dist = length(q - (o + R * vec2(cos(at), sin(at))));
    float ink  = MouthIn(dist - 0.5 * p.creaseWidth * (1.0 - s * s), aa) * MouthSwap(p);
    return mix(albedo, p.creaseColor, ink);
}
// ---- end MouthPaint ----
`;
        var FRAG_HEAD = [
            'uniform mat4 featMat[4];',   // left brow, right brow, mouth, chin
            'uniform vec4 featRow[4];',   // placed, -, aspect, -
            'uniform vec4 featShow;',     // show regions, selected, show weights
            'varying vec3 vBindPos;',
            'varying float vFaceW;',
            'uniform vec4 mouthA;',       // on, open, edge, show lip line
            'uniform vec4 mouthLip;',     // lipY, lipCornerY, lipHalfWidth, openDepth
            'uniform vec4 mouthInt;',     // interior + upper teeth
            'uniform vec4 mouthTeeth;',   // teeth + lower teeth
            'uniform vec4 mouthTongue;',  // tongue + upper tooth height
            'uniform vec4 mouthLips;',    // lips + lip thickness
            'uniform vec4 mouthMisc;',    // teeth width, round, tongue up, bite
            'uniform vec4 mouthOutline;', // outline + lower tooth height
            'uniform vec4 mouthSkin;',    // skin + mask margin
            'uniform vec4 mouthSkinMR;',  // the skin's roughness, metalness, known (1/0), -
            'float gMouthSurface;',       // how much of this fragment the mouth covers: mask or drawn mouth
            'uniform vec4 mouthTooth;',   // tooth width, tooth gap, crease length, crease width
            'uniform vec4 mouthCrease;',  // crease curve, angle (radians), offset, -
            'uniform vec3 mouthCreaseColor;',
            'uniform vec4 mouthLipLook;', // lip opacity, lip arc, -, -
            MOUTH_CORE,
            // MouthOverlay.glsl's ApplyMouth, with the lip line drawn on top for lining up.
            'vec3 ApplyMouth(vec3 albedo) {',
            '    if (featRow[2].x < 0.5 || (mouthA.x < 0.5 && mouthA.w < 0.5)) return albedo;',
            '    vec3 f = (featMat[2] * vec4(vBindPos, 1.0)).xyz;',
            '    float slab = step(abs(f.z), 1.0);',
            '    float aa = length(fwidth(f.xy)) * 0.75;',
            '    if (mouthA.x > 0.5) {',
            '        MouthParams p;',
            '        p.open = mouthA.y; p.edge = mouthA.z; p.lip = mouthLip;',
            '        p.interior = mouthInt.rgb; p.teethUpper = mouthInt.a;',
            '        p.teeth = mouthTeeth.rgb; p.teethLower = mouthTeeth.a;',
            '        p.tongue = mouthTongue.rgb; p.upperTooth = mouthTongue.a;',
            '        p.lips = mouthLips.rgb; p.lipThickness = mouthLips.a;',
            '        p.outline = mouthOutline.rgb; p.lowerTooth = mouthOutline.a;',
            '        p.skin = mouthSkin.rgb; p.maskMargin = mouthSkin.a;',
            '        p.teethWidth = mouthMisc.x; p.round = mouthMisc.y; p.tongueUp = mouthMisc.z; p.bite = mouthMisc.w;',
            '        p.toothWidth = mouthTooth.x; p.toothGap = mouthTooth.y;',
            '        p.creaseLength = mouthTooth.z; p.creaseWidth = mouthTooth.w;',
            '        p.creaseCurve = mouthCrease.x; p.creaseAngle = mouthCrease.y; p.creaseOffset = mouthCrease.z;',
            '        p.creaseColor = mouthCreaseColor; p.baseOpen = mouthCrease.w;',
            '        p.lipOpacity = mouthLipLook.x; p.lipArc = mouthLipLook.y; p.alwaysDrawn = mouthLipLook.z;',
            '        MouthShape g = MouthShapeAt(f.xy, p);',
            '        albedo = mix(albedo, MouthCreases(MouthMask(albedo, f.xy, aa, p, g), f.xy, aa, p, g), slab);',
            '        vec4 m = MouthPaint(f.xy, aa, p, g);',
            '        albedo = mix(albedo, m.rgb, m.a * slab);',
            '        gMouthSurface = max(MouthMaskZone(f.xy, aa, p, g), m.a) * slab;',
            '    }',
            '    float hw = max(mouthLip.z, 1e-4);',
            '    float t = clamp(f.x / hw, -1.0, 1.0);',
            '    float top = mix(mouthLip.x, mouthLip.y, t * t);',
            '    float line = (1.0 - smoothstep(aa, 2.0 * aa, abs(f.y - top))) * step(abs(f.x), hw) * slab;',
            '    return mix(albedo, vec3(1.0, 0.85, 0.0), line * mouthA.w);',
            '}',
            'vec3 TintFace(vec3 c) {',
            '    for (int f = 0; f < 4; ++f) {',
            '        if (featRow[f].x < 0.5) continue;',
            '        vec3 fp = (featMat[f] * vec4(vBindPos, 1.0)).xyz;',
            '        float r = length(vec2(fp.x / max(featRow[f].z, 1e-4), fp.y));',
            '        float inside = (1.0 - step(1.0, r)) * step(abs(fp.z), 1.0);',
            '        float tint = featShow.x * (f == int(featShow.y + 0.5) ? 0.35 : 0.15);',
            '        c = mix(c, vec3(0.0, 1.0, 1.0), inside * tint);',
            '    }',
            '    return mix(c, vec3(1.0, 0.1, 0.6), clamp(vFaceW, 0.0, 1.0) * 0.7 * featShow.z);',
            '}', ''
        ].join('\n');
        var FRAG_BODY = '\ngMouthSurface = 0.0;\ndiffuseColor.rgb = TintFace(ApplyMouth(diffuseColor.rgb));\n';
        // Where the mouth covers the paint, the skin's roughness and metalness, not the paint's (a
        // painted grin's teeth can be glossier than the face). Preview only: the game's shading has no
        // roughness or metalness maps.
        var MOUTH_MR_ROUGH = '\nroughnessFactor = mix(roughnessFactor, mouthSkinMR.x, gMouthSurface * mouthSkinMR.z);\n';
        var MOUTH_MR_METAL = '\nmetalnessFactor = mix(metalnessFactor, mouthSkinMR.y, gMouthSurface * mouthSkinMR.z);\n';

        function vec4s(n) { var a = []; for (var i = 0; i < n; i++) a.push(new THREE.Vector4()); return a; }
        var U = {
            faceBoneIdx: { value: [new THREE.Vector4(-1, -1, -1, -1), new THREE.Vector4(-1, -1, -1, -1)] },
            featMat: { value: [new THREE.Matrix4(), new THREE.Matrix4(), new THREE.Matrix4(), new THREE.Matrix4()] },
            featRow: { value: vec4s(4) },
            featShow: { value: new THREE.Vector4() },
            mouthA: { value: new THREE.Vector4() },
            mouthLip: { value: new THREE.Vector4() },
            mouthInt: { value: new THREE.Vector4() },
            mouthTeeth: { value: new THREE.Vector4() },
            mouthTongue: { value: new THREE.Vector4() },
            mouthLips: { value: new THREE.Vector4() },
            mouthMisc: { value: new THREE.Vector4() },
            mouthOutline: { value: new THREE.Vector4() },
            mouthSkin: { value: new THREE.Vector4() },
            mouthSkinMR: { value: new THREE.Vector4() },
            mouthTooth: { value: new THREE.Vector4() },
            mouthCrease: { value: new THREE.Vector4() },
            mouthCreaseColor: { value: new THREE.Vector3() },
            mouthLipLook: { value: new THREE.Vector4(1, 1, 0, 0) }
        };

        function patchMaterial(mat) {
            mat.onBeforeCompile = function (shader) {
                for (var k in U) shader.uniforms[k] = U[k];
                shader.vertexShader = VERT_HEAD + shader.vertexShader.replace('#include <skinning_vertex>', '#include <skinning_vertex>' + VERT_BODY);
                shader.fragmentShader = FRAG_HEAD + shader.fragmentShader
                    .replace('#include <map_fragment>', '#include <map_fragment>' + FRAG_BODY)
                    .replace('#include <roughnessmap_fragment>', '#include <roughnessmap_fragment>' + MOUTH_MR_ROUGH)
                    .replace('#include <metalnessmap_fragment>', '#include <metalnessmap_fragment>' + MOUTH_MR_METAL);
            };
            mat.needsUpdate = true;
        }

        // ---- The face bones (FaceBones.cpp) ----
        // FacePreset order: Neutral, Focus, Effort, Surprise, Pain, Joy, Dejected.
        var PRESETS = [
            {}, { furrow: 0.4, grit: 0.2 },
            { furrow: 0.8, grit: 0.7, open: 0.15, teethUpper: 1, teethLower: 1 },
            { raise: 1.0, pucker: 0.6, open: 0.55 },
            { furrow: 1.0, sad: 0.3, grit: 0.8, frown: 0.4, open: 0.25, teethUpper: 1, teethLower: 1 },
            { raise: 0.4, smile: 1.0, open: 0.35, teethUpper: 1 },
            { sad: 1.0, frown: 0.8 }
        ];
        function w(face, key) { return face[key] || 0; }

        // The game's Viseme order and PoseFor table (Viseme.cpp) -- keep in step.
        var VISEME_KEYS = ['open', 'round', 'pucker', 'grit', 'teethUpper', 'teethLower', 'tongueUp'];
        var VISEMES = [
            [0, 0, 0, 0, 0, 0, 0],             // Rest
            [0, 0, 0.15, 0, 0, 0, 0],          // MBP
            [0.70, 0, 0, 0, 0.8, 0, 0],        // AA
            [0.45, 0, 0, 0.3, 1, 0, 0],        // EH
            [0.20, 0, 0, 0.7, 1, 1, 0],        // EE
            [0.55, 0.7, 0.5, 0, 0.3, 0, 0],    // OH
            [0.25, 1, 1, 0, 0, 0, 0],          // OO
            [0.12, 0, 0, 0.1, 1, 0, 0],        // FV
            [0.28, 0, 0, 0.1, 0.8, 0, 1],      // TL
            [0.12, 0, 0, 0.4, 1, 1, 0],        // SZ
            [0.18, 0.3, 0.5, 0, 1, 1, 0],      // SH
            [0.35, 0, 0, 0.1, 0.6, 0, 0],      // KG
            [0.25, 0.4, 0.4, 0, 0.5, 0, 0],    // R
            [0.35, 0.2, 0.1, 0, 0.3, 0, 0]     // HH
        ];
        function faceFor(s) {
            var f = Object.assign({}, PRESETS[s ? s.preset : 0] || {});
            var v = s ? VISEMES[s.viseme] : null;
            if (v) VISEME_KEYS.forEach(function (k, i) { f[k] = v[i]; });
            return f;
        }

        // A move (body axes, mesh units) and a turn about body z, as FaceBones::MotionFor.
        function motionFor(d, face, s, openFromRest) {
            var move = [0, 0, 0], turn = 0;
            if (d.face === 'brow') {
                var innerDrop = (0.40 * w(face, 'sad') - 0.40 * w(face, 'furrow')) * s * d.unit;
                turn = d.reach > 0 ? d.inner * Math.atan2(innerDrop, d.reach) : 0;
                move[1] = 0.35 * w(face, 'raise') * s * d.unit;
                move[0] = d.inner * 0.05 * w(face, 'furrow') * s * d.unit;
            } else if (d.face === 'mouth') {
                // Shaped by where along the mouth the bone sits: a smile lifts the corners, the middle dips.
                var a = d.across, a2 = a * a;
                move[1] = ((0.45 * w(face, 'smile') - 0.40 * w(face, 'frown')) * a2 - 0.10 * w(face, 'smile') * (1 - a2)) * s * d.unit;
                move[0] = a * (0.12 * w(face, 'grit') - 0.30 * w(face, 'pucker')) * s * d.halfWidth;
            } else if (d.face === 'chin') {
                // The panel's Chin drop, not the saved one, so it can be tuned before saving.
                move[1] = -(state ? state.chinDrop : d.drop) * openFromRest * d.unit;
            }
            return { move: move, turn: turn };
        }

        var faceBones = [];   // { bone, d (extras), restPos, restQuat, toLocal (mesh -> parent-local), axis }
        function onLoaded() {
            scene.updateMatrixWorld(true);
            var idx = [];
            meshes.forEach(function (m) {
                if (!m.skeleton) return;
                m.skeleton.bones.forEach(function (b, i) {
                    if (!b.userData || !b.userData.face) return;
                    if (idx.length < 8 && idx.indexOf(i) < 0) idx.push(i);
                    if (faceBones.some(function (x) { return x.bone === b; })) return;
                    // Mesh units -> the parent's frame at rest, as FaceBones::Build.
                    var meshToWorld = new THREE.Matrix4().multiplyMatrices(b.matrixWorld, m.skeleton.boneInverses[i]);
                    var parentInv = new THREE.Matrix4().copy(b.parent.matrixWorld).invert();
                    var toLocal = new THREE.Matrix3().setFromMatrix4(new THREE.Matrix4().multiplyMatrices(parentInv, meshToWorld));
                    faceBones.push({ bone: b, d: b.userData, restPos: b.position.clone(), restQuat: b.quaternion.clone(),
                                     toLocal: toLocal, axis: new THREE.Vector3(0, 0, 1).applyMatrix3(toLocal).normalize() });
                });
            });
            for (var k = 0; k < 8; k++) U.faceBoneIdx.value[k >> 2].setComponent(k & 3, k < idx.length ? idx[k] : -1);
            initMouthPointDrag();
            post({ action: 'loaded', bones: faceBones.length });
        }

        // ---- Mouth points (FaceBonesEditor.MouthPoints.cs): dots on the picked points, the line
        // through them, and dragging a dot along the surface ----
        var pointDots = [], pointLine = null, dragging = -1, pointHintShown = false;
        // A bind-pose mesh point where it's drawn now: through the head bone, as three's skinning does
        // (the face rides the head), so a dot sits on the painted feature whatever the rest pose.
        function meshToWorld(p) {
            var m = meshes[0], v = new THREE.Vector3(p[0], p[1], p[2]);
            var i = m.isSkinnedMesh && headBone ? m.skeleton.bones.indexOf(headBone) : -1;
            if (i < 0) return v.applyMatrix4(m.matrixWorld);
            return v.applyMatrix4(m.bindMatrix).applyMatrix4(m.skeleton.boneInverses[i])
                    .applyMatrix4(headBone.matrixWorld).applyMatrix4(m.bindMatrixInverse).applyMatrix4(m.matrixWorld);
        }

        function showMouthPoints(points) {
            if (!meshes.length) return;
            while (pointDots.length > points.length) scene.remove(pointDots.pop());
            while (pointDots.length < points.length) {
                var dot = new THREE.Mesh(new THREE.SphereGeometry(modelHeight * 0.0035, 12, 8),
                                         new THREE.MeshBasicMaterial({ color: 0xffd400, depthTest: false }));
                dot.renderOrder = 10;
                scene.add(dot);
                pointDots.push(dot);
            }
            points.forEach(function (p, i) { pointDots[i].position.copy(meshToWorld(p)); });
            if (pointLine) { scene.remove(pointLine); pointLine = null; }
            if (points.length > 1) {
                pointLine = new THREE.Line(new THREE.BufferGeometry().setFromPoints(points.map(meshToWorld)),
                                           new THREE.LineBasicMaterial({ color: 0xffd400, depthTest: false }));
                pointLine.renderOrder = 10;
                scene.add(pointLine);
            }
        }

        // The dot under the pointer (within 10 px), or -1.
        function dotAt(event) {
            var rect = canvas.getBoundingClientRect();
            for (var i = 0; i < pointDots.length; i++) {
                var v = pointDots[i].position.clone().project(camera);
                var x = (v.x + 1) / 2 * rect.width + rect.left, y = (1 - v.y) / 2 * rect.height + rect.top;
                if (Math.abs(x - event.clientX) < 10 && Math.abs(y - event.clientY) < 10) return i;
            }
            return -1;
        }

        function surfaceAt(event) {
            var rect = canvas.getBoundingClientRect();
            var ndc = new THREE.Vector2(((event.clientX - rect.left) / rect.width) * 2 - 1,
                                        -((event.clientY - rect.top) / rect.height) * 2 + 1);
            var ray = new THREE.Raycaster();
            ray.setFromCamera(ndc, camera);
            var hits = ray.intersectObjects(meshes, false);
            return hits.length && hits[0].face ? meshPointFromHit(hits[0]) : null;
        }

        // Capture phase: decided before the orbit controls see the press.
        function initMouthPointDrag() {
            canvas.addEventListener('pointerdown', function (event) {
                if (!state || state.pickPoints) return;
                dragging = dotAt(event);
                if (dragging >= 0) { controls.enabled = false; event.stopPropagation(); }
            }, true);
            canvas.addEventListener('pointermove', function (event) {
                if (dragging < 0) return;
                var p = surfaceAt(event);
                if (p) { state.mouthPoints[dragging] = p; showMouthPoints(state.mouthPoints); }
            });
            window.addEventListener('pointerup', function () {
                if (dragging < 0) return;
                post({ action: 'mouthPointMoved', i: dragging, p: state.mouthPoints[dragging] });
                dragging = -1;
                controls.enabled = true;
            });
        }

        function applyFace() {
            var face = faceFor(state), s = state ? state.strength : 1;
            var open = state ? mouthOpen(state) - (state.mouth.baseOpen || 0) : 0;   // from the painted opening
            faceBones.forEach(function (fb) {
                var m = motionFor(fb.d, face, s, open);
                fb.bone.position.copy(fb.restPos).add(new THREE.Vector3(m.move[0], m.move[1], m.move[2]).applyMatrix3(fb.toLocal));
                fb.bone.quaternion.copy(new THREE.Quaternion().setFromAxisAngle(fb.axis, m.turn).multiply(fb.restQuat));
            });
        }

        // ---- State from C# (FaceBonesEditor.PushState) ----
        var state = null;
        window.setFace = function (s) {
            state = s;
            for (var f = 0; f < 4; f++) {
                var R = s.regions[f], row = U.featRow.value[f];
                if (!R.on || R.r[1] <= 0 || R.r[2] <= 0) { row.set(0, 0, 1, 0); continue; }
                var back = R.r[2], front = Math.min(R.f, back), half = Math.max(0.5 * (front + back), 1e-6);
                U.featMat.value[f].makeTranslation(-R.c[0], -R.c[1], -R.c[2])
                    .premultiply(new THREE.Matrix4().makeRotationZ(-(R.t || 0)))   // a tilted brow region
                    .premultiply(new THREE.Matrix4().makeTranslation(0, 0, -0.5 * (front - back)))
                    .premultiply(new THREE.Matrix4().makeScale(1 / R.r[1], 1 / R.r[1], 1 / half));
                row.set(1, 0, R.r[0] / R.r[1], 0);
            }
            U.featShow.value.set(s.showRegion ? 1 : 0, s.selected, s.showWeights ? 1 : 0, 0);
            applyMouth(s);
            applyFace();
            // The dots and line: their own toggle (Show mouth points), and always while picking.
            showMouthPoints(s.showPoints || s.pickPoints ? (s.mouthPoints || []) : []);
            if (s.pickPoints) {
                setHint('Click the mouth: point ' + ((s.mouthPoints || []).length + 1) +
                        ' of 5 - both corners and three along the lip line between them.');
                pointHintShown = true;
            } else if (pointHintShown) { setHint(null); pointHintShown = false; }
        };

        // As MouthStencil::Pack: the preset's opening times the strength, unless Test open is up.
        // The chin drops by the same opening (FaceBones::Apply).
        // As EffectiveMouthOpen (FaceExpression.cpp): an expression only opens a painted-open mouth
        // wider; a held mouth shape (talking) sets it outright. Test open is in charge once moved off
        // the painted opening it starts at.
        function testing(M) { return Math.abs(M.testOpen - (M.baseOpen || 0)) > 0.005; }
        function mouthOpen(s) {
            var M = s.mouth, base = M.baseOpen || 0;
            if (testing(M)) return Math.min(Math.max(M.testOpen, 0), 1);
            var o = Math.min(Math.max(w(faceFor(s), 'open') * s.strength, 0), 1);
            var rest = Math.max(o, base), talk = s.viseme >= 0 ? 1 : 0;
            return rest + (o - rest) * talk;
        }
        function linear(c, a) { return new THREE.Vector4(srgbToLinear(c[0]), srgbToLinear(c[1]), srgbToLinear(c[2]), a); }
        function applyMouth(s) {
            var M = s.mouth, face = faceFor(s), forced = testing(M);
            var open = mouthOpen(s);
            U.mouthA.value.set(M.on && (M.alwaysDrawn > 0.5 || Math.abs(open - (M.baseOpen || 0)) > 1e-3) ? 1 : 0, open, M.edge,
                               s.showRegion && M.on ? 1 : 0);
            U.mouthLip.value.set(M.lipY, M.lipCornerY, M.lipHalfWidth, M.openDepth);
            // The teeth showing at rest unless a held mouth shape (talking) sets them, as MouthStencil::Pack.
            var talk = s.viseme >= 0 ? 1 : 0;
            function teeth(shown, rest) { var t = Math.max(shown, rest || 0); return t + (shown - t) * talk; }
            U.mouthInt.value.copy(linear(M.interior, forced ? M.teethUpper : teeth(w(face, 'teethUpper'), M.restTeethUpper)));
            U.mouthTeeth.value.copy(linear(M.teeth, forced ? M.teethLower : teeth(w(face, 'teethLower'), M.restTeethLower)));
            U.mouthTongue.value.copy(linear(M.tongue, M.teethHeight));
            U.mouthLips.value.copy(linear(M.lips, M.lipThickness));
            U.mouthMisc.value.set(M.teethWidth, Math.min(w(face, 'round'), 1), Math.min(w(face, 'tongueUp'), 1), M.bite);
            U.mouthOutline.value.copy(linear(M.outline, M.lowerTeethHeight));
            U.mouthSkin.value.copy(M.skin ? linear(M.skin, M.maskMargin) : new THREE.Vector4(0, 0, 0, 0));   // no skin: no mask
            if (M.skinRoughness != null && M.skinMetalness != null) U.mouthSkinMR.value.set(M.skinRoughness, M.skinMetalness, 1, 0);
            else U.mouthSkinMR.value.set(0, 0, 0, 0);
            U.mouthTooth.value.set(M.toothWidth, M.toothGap, M.creaseLength, M.creaseWidth);
            U.mouthCrease.value.set(M.creaseCurve, M.creaseAngle * Math.PI / 180, M.creaseOffset, M.baseOpen || 0);
            var cc = linear(M.crease, 0);
            U.mouthCreaseColor.value.set(cc.x, cc.y, cc.z);
            U.mouthLipLook.value.set(M.lipOpacity != null ? M.lipOpacity : 1, M.lipArc != null ? M.lipArc : 1, M.alwaysDrawn || 0, 0);
        }

        // Colour eyedropper (FaceBonesEditor.StartMouthEyedropper): the next click samples the texture
        // under it for `pickTarget` (Lips, Outline, Skin) instead of placing a region.
        var pickTarget = null;
        window.setPickMode = function (target) {
            pickTarget = target;
            setHint(target ? 'Click the model to take the ' + target.toLowerCase() + ' colour.' : null);
        };

        // The mask's skin colour when none's been picked: the texture on the cheek just past the
        // mouth region's side, found by a ray straight at the face from in front (bind pose, mesh
        // +z forward, as the anchors are). Tries both sides; posts it as a Skin pick.
        window.sampleSkinBeside = function (mrOnly) {
            var R = state && state.regions[2];
            if (!R || !R.on || !meshes.length) return;
            // Past a corner first; a mouth as wide as the face (a painted grin) has no cheek there, so
            // then closer in, and up on the cheek above the corner.
            var tries = [[1.3, 0], [1.0, 1.5], [0.8, 2.5], [0.6, 3.5]];
            for (var t = 0; t < tries.length * 2; t++) {
                var side = t % 2 ? -1 : 1, at = tries[t >> 1];
                var x = R.c[0] + side * R.r[0] * at[0], y = R.c[1] + R.r[1] * at[1];
                var from = meshToWorld([x, y, R.c[2] + modelHeight]);
                var to = meshToWorld([x, y, R.c[2]]);
                var hits = new THREE.Raycaster(from, to.clone().sub(from).normalize()).intersectObjects(meshes, true);
                if (!hits.length) continue;
                var mat = materialOf(hits[0]);
                var rgb = sampleTexture(hits[0], mat && mat.map);
                if (rgb) { post({ action: 'colorPicked', target: 'Skin', rgb: rgb, mr: sampleMR(hits[0]), mrOnly: !!mrOnly }); return; }
            }
        };

        function onPick(first, chosen, event) {
            if (pickTarget) {
                var target = pickTarget;
                setPickMode(null);
                try {
                    var mat = materialOf(first);
                    var rgb = sampleTexture(first, mat && mat.map);
                    if (rgb) post({ action: 'colorPicked', target: target, rgb: rgb, mr: sampleMR(first) });
                    else showError('No texture under that point to sample.');
                } catch (err) { showError('Could not sample the texture: ' + err.message); }
                return;
            }
            if (state && state.pickPoints) { post({ action: 'mouthPoint', p: meshPointFromHit(chosen) }); return; }
            post({ action: 'clicked', p: meshPointFromHit(chosen) });
        }
";
    }
}
