namespace GlbMerger
{
    // EyeAnchorEditor's script for the shared preview page (FacePreviewPage). The model's materials
    // are patched with a copy of the game's eye overlay (FootballRoguelite shaders/EyeOverlay.glsl,
    // model.frag's ApplyEyes and EyeRig::PupilOffset) - keep the two in step, or what is tuned here
    // stops being what the game draws.
    public partial class EyeAnchorEditor
    {
        private static string BuildViewerHtml(string previewFileName) => FacePreviewPage.Build(
            previewFileName, "Click an eye to place the selected anchor. Drag to orbit, wheel to zoom.", ViewerScript);

        private const string ViewerScript = @"
        // ---- The game's overlay (EyeOverlay.glsl), uniform-fed instead of UBO-fed ----
        var EYE_VERT_HEAD = [
            'uniform mat4 eyeL;', 'uniform mat4 eyeR;',
            'varying vec3 vEyeL;', 'varying vec3 vEyeR;', ''
        ].join('\n');
        var EYE_VERT_BODY = '\nvEyeL = (eyeL * vec4(position, 1.0)).xyz;\nvEyeR = (eyeR * vec4(position, 1.0)).xyz;\n';
        var EYE_FRAG_HEAD = [
            'uniform vec4 eyeRowL;', 'uniform vec4 eyeRowR;',   // pupil.xy, aspect, on
            'uniform vec4 eyeLook0;', 'uniform vec4 eyeLook1;', 'uniform vec4 eyeLook2;',
            'uniform vec4 eyeLook3;',   // fill L, fill R, rim width, rim strength
            'uniform vec4 eyeIris;',    // iris colour (linear), iris radius
            'uniform vec4 eyeLid0;',    // lid colour (linear), lash width
            'uniform vec4 eyeLid1;',    // upper, lower closure, lash strength, lids on
            'uniform vec4 eyeExtra;',   // cleanup margin
            // Preview only (the game has no metallic/roughness): the lid skin's own roughness and
            // metalness, sampled with its colour, so lids stop shining like the eye did.
            'uniform vec4 eyeLidMR;',   // roughness, metalness, use (0/1)
            'float gLidCover;',         // PaintLids -> PaintEye: lid + lash coverage of this eye
            'float gLidSurface;',       // how much of this fragment is lid skin, both eyes
            'const float kLidMeet = -0.2;',
            'vec3 PaintLids(vec3 c, vec2 q) {',
            '    float arc = sqrt(max(1.0 - q.x * q.x, 0.0));',
            '    float du = q.y - mix(1.0, kLidMeet, eyeLid1.x) * arc;',
            '    float dl = mix(-1.0, kLidMeet, eyeLid1.y) * arc - q.y;',
            '    float aaU = max(fwidth(du), 1e-4);',
            '    float aaL = max(fwidth(dl), 1e-4);',
            '    float on = eyeLid1.w;',
            '    float lid = max(smoothstep(-aaU, aaU, du), smoothstep(-aaL, aaL, dl));',
            '    c = mix(c, eyeLid0.rgb, lid * on);',
            '    float wU = eyeLid0.a, wL = 0.5 * eyeLid0.a;',
            '    float lashU = smoothstep(-wU - aaU, -wU + aaU, du) * (1.0 - smoothstep(-aaU, aaU, du));',
            '    float lashL = smoothstep(-wL - aaL, -wL + aaL, dl) * (1.0 - smoothstep(-aaL, aaL, dl));',
            '    gLidCover = max(lid, max(lashU, 0.5 * lashL)) * on;',
            '    return mix(c, eyeLook0.rgb, max(lashU, 0.5 * lashL) * eyeLid1.z * on);',
            '}',
            'varying vec3 vEyeL;', 'varying vec3 vEyeR;',
            'float EyeDisc(float dist, float radius) {',
            '    float aa = max(fwidth(dist), 1e-4);',
            '    return 1.0 - smoothstep(radius - aa, radius + aa, dist);',
            '}',
            'float Saturation(vec3 c) {',
            '    float mx = max(max(c.r, c.g), c.b);',
            '    float mn = min(min(c.r, c.g), c.b);',
            '    return (mx - mn) / max(mx, 1e-4);',
            '}',
            'float PaintedEye(vec3 albedo) {',
            '    float mx = max(max(albedo.r, albedo.g), albedo.b);',
            '    float sat = Saturation(albedo);',
            '    float pale = 1.0 - smoothstep(eyeLook2.x, eyeLook2.y, sat);',
            '    float dark = 1.0 - smoothstep(eyeLook2.z, 2.0 * eyeLook2.z, mx);',
            '    float paler = smoothstep(0.08, 0.20, Saturation(eyeLid0.rgb) - sat) * eyeLid1.w;',
            '    return max(max(pale, dark), paler);',
            '}',
            // EyeOverlay.glsl LimitWindow: the resting lid edges grown by the limits.
            'float LimitWindow(vec2 q) {',
            '    float hU = mix(1.0, kLidMeet, eyeLook3.y) + eyeExtra.y;',
            '    float hL = mix(-1.0, kLidMeet, eyeExtra.w) - eyeExtra.z;',
            '    float wU = 1.0 + eyeExtra.y, wL = 1.0 + eyeExtra.z;',
            '    float dUp = q.y - hU * sqrt(max(1.0 - (q.x * q.x) / (wU * wU), 0.0));',
            '    float dDn = hL * sqrt(max(1.0 - (q.x * q.x) / (wL * wL), 0.0)) - q.y;',
            '    float aaU = max(fwidth(dUp), 1e-4), aaD = max(fwidth(dDn), 1e-4);',
            '    return (1.0 - smoothstep(-aaU, aaU, dUp)) * (1.0 - smoothstep(-aaD, aaD, dDn));',
            '}',
            'vec3 PaintEye(vec3 albedo, vec3 e, vec2 pupil, float aspect, float on, float fill) {',
            '    float r = length(vec2(e.x / max(aspect, 1e-4), e.y));',
            '    float rimAA = max(fwidth(r), 1e-4);',
            '    float window = LimitWindow(vec2(e.x / max(aspect, 1e-4), e.y));',
            '    float inside = EyeDisc(r, 1.0) * step(abs(e.z), 1.0) * on * window;',
            '    float painted = PaintedEye(albedo);',
            '    float eyeMask = inside * mix(painted, 1.0, fill);',
            '    float halo = EyeDisc(r, 1.0 + eyeExtra.x) * step(abs(e.z), 1.0) * on * window;',
            '    vec3 c = eyeLook1.rgb;',
            '    float fromPupil = length(e.xy - pupil);',
            '    float irisMask = EyeDisc(fromPupil, eyeIris.a);',
            '    float limbal = smoothstep(0.55, 1.0, fromPupil / max(eyeIris.a, 1e-4));',
            '    c = mix(c, eyeIris.rgb * mix(1.0, 0.5, limbal), irisMask);',
            '    float pupilMask = EyeDisc(fromPupil, eyeLook0.a);',
            '    c = mix(c, eyeLook0.rgb, pupilMask);',
            '    vec2 glint = pupil + vec2(-0.35, 0.35) * eyeLook0.a;',
            '    float glintMask = EyeDisc(length(e.xy - glint), 0.28 * eyeLook0.a) * pupilMask * eyeLook1.a;',
            '    c = mix(c, vec3(1.0), glintMask);',
            '    float rim = smoothstep(1.0 - eyeLook3.z - rimAA, 1.0 - eyeLook3.z + rimAA, r);',
            '    c = mix(c, eyeLook0.rgb, rim * fill * eyeLook3.w);',
            '    c = PaintLids(c, vec2(e.x / max(aspect, 1e-4), e.y));',
            '    c = mix(albedo, c, eyeMask);',
            '    float margin = max(halo - inside, 0.0);',
            '    float fade = 1.0 - smoothstep(1.0, 1.0 + max(eyeExtra.x, 1e-4), r);',
            '    float lidMax = max(max(eyeLid0.r, eyeLid0.g), eyeLid0.b);',
            '    float offLid = smoothstep(0.10, 0.30, length(albedo - eyeLid0.rgb) / (lidMax + 0.02));',
            '    float cleanup = margin * max(painted, offLid * fade) * eyeLid1.w;',
            '    c = mix(c, eyeLid0.rgb, cleanup);',
            '    gLidSurface = max(gLidSurface, eyeMask * gLidCover + cleanup);',
            '    c = mix(c, vec3(1.0, 0.0, 1.0), margin * 0.25 * eyeLook2.w);',
            '    return mix(c, vec3(1.0, 0.0, 1.0), inside * 0.5 * eyeLook2.w);',
            '}', ''
        ].join('\n');
        var EYE_MR_ROUGH = '\nroughnessFactor = mix(roughnessFactor, eyeLidMR.x, gLidSurface * eyeLidMR.z);\n';
        var EYE_MR_METAL = '\nmetalnessFactor = mix(metalnessFactor, eyeLidMR.y, gLidSurface * eyeLidMR.z);\n';
        var EYE_FRAG_BODY = '\ngLidCover = 0.0; gLidSurface = 0.0;\ndiffuseColor.rgb = PaintEye(PaintEye(diffuseColor.rgb, vEyeL, eyeRowL.xy, eyeRowL.z, eyeRowL.w, eyeLook3.x),' +
                            ' vEyeR, eyeRowR.xy, eyeRowR.z, eyeRowR.w, eyeLook3.x);\n';

        // Game defaults (EyeLook.h); the look itself is tuned in the game.
        var eyeUniforms = {
            eyeL: { value: new THREE.Matrix4() }, eyeR: { value: new THREE.Matrix4() },
            eyeRowL: { value: new THREE.Vector4() }, eyeRowR: { value: new THREE.Vector4() },
            eyeLook0: { value: new THREE.Vector4(0.05, 0.04, 0.04, 0.45) },
            eyeLook1: { value: new THREE.Vector4(0.96, 0.96, 0.94, 1.0) },
            eyeLook2: { value: new THREE.Vector4(0.15, 0.32, 0.05, 0.0) },
            eyeLook3: { value: new THREE.Vector4(1.0, 1.0, 0.15, 0.2) },
            eyeIris: { value: new THREE.Vector4(0.13, 0.05, 0.015, 0.70) },
            eyeLid0: { value: new THREE.Vector4(0, 0, 0, 0.10) },
            eyeLid1: { value: new THREE.Vector4(0.18, 0.05, 0.85, 0) },
            eyeExtra: { value: new THREE.Vector4(0.15, 3, 3, 0) },   // cleanup, limit above, limit below
            eyeLidMR: { value: new THREE.Vector4(1, 0, 0, 0) }
        };

        // Lids, animated every frame (EyeBlink / EyeRig::LidClosure, preview-sized: a blink every
        // ~2-4 s, 0.16 s long, shutting in its first 40%).
        var BLINK_SEC = 0.16;
        var lids = { on: false, rest: 0.18, lowerRest: 0.05, autoBlink: true, testClosure: 0, blinkT: -1, untilBlink: 1.5 };
        function smooth01(t) { t = Math.min(Math.max(t, 0), 1); return t * t * (3 - 2 * t); }
        function updateLids(dt) {
            var blink = 0;
            if (lids.autoBlink) {
                lids.untilBlink -= dt;
                if (lids.blinkT < 0 && lids.untilBlink <= 0) lids.blinkT = 0;
                if (lids.blinkT >= 0) {
                    var shut = 0.4 * BLINK_SEC;
                    blink = lids.blinkT < shut ? smooth01(lids.blinkT / shut)
                                               : 1 - smooth01((lids.blinkT - shut) / (BLINK_SEC - shut));
                    lids.blinkT += dt;
                    if (lids.blinkT >= BLINK_SEC) { lids.blinkT = -1; lids.untilBlink = 2 + Math.random() * 2; }
                }
            }
            var close = lids.testClosure > 0 ? lids.testClosure : blink;
            var upper = lids.rest + (1 - lids.rest) * close;
            var lower = lids.lowerRest + (1 - lids.lowerRest) * close;
            var l1 = eyeUniforms.eyeLid1.value;
            l1.x = upper; l1.y = lower; l1.w = lids.on ? 1 : 0;
        }

        function patchMaterial(mat) {
            mat.extensions = mat.extensions || {};
            mat.extensions.derivatives = true;
            mat.onBeforeCompile = function (shader) {
                for (var k in eyeUniforms) shader.uniforms[k] = eyeUniforms[k];
                shader.vertexShader = EYE_VERT_HEAD + shader.vertexShader.replace(
                    '#include <begin_vertex>', '#include <begin_vertex>' + EYE_VERT_BODY);
                shader.fragmentShader = EYE_FRAG_HEAD + shader.fragmentShader
                    .replace('#include <map_fragment>', '#include <map_fragment>' + EYE_FRAG_BODY)
                    .replace('#include <roughnessmap_fragment>', '#include <roughnessmap_fragment>' + EYE_MR_ROUGH)
                    .replace('#include <metalnessmap_fragment>', '#include <metalnessmap_fragment>' + EYE_MR_METAL);
            };
            mat.needsUpdate = true;
        }

        // EyeRig::PupilOffset.
        function pupilOffset(yaw, pitch, aspect, pupilRadius, maxOffset) {
            var qx = Math.sin(yaw) * maxOffset, qy = Math.sin(pitch) * maxOffset;
            var reach = Math.max(0, 1 - pupilRadius), len = Math.hypot(qx, qy);
            if (len > reach && len > 1e-6) { qx *= reach / len; qy *= reach / len; }
            return [qx * aspect, qy];
        }

        // Pushed from C# (EyeAnchorEditor.PushEyes): centres / half-extents in mesh units.
        window.setEyes = function (s) {
            var rows = [eyeUniforms.eyeRowL.value, eyeUniforms.eyeRowR.value];
            var mats = [eyeUniforms.eyeL.value, eyeUniforms.eyeR.value];
            // EyeRig::Build: each eye lifts its outer corner, away from the midpoint between them.
            var bothOn = s.eyes[0].on && s.eyes[1].on;
            var midline = bothOn ? 0.5 * (s.eyes[0].c[0] + s.eyes[1].c[0]) : 0;
            for (var side = 0; side < 2; side++) {
                var e = s.eyes[side];
                if (!e.on || e.r[1] <= 0 || e.r[2] <= 0) { rows[side].set(0, 0, 1, 0); continue; }
                var aspect = e.r[0] / e.r[1];
                // The slab runs r[2] behind the centre and f in front (EyeRig::Build).
                var back = e.r[2], front = Math.min(e.f, back), half = Math.max(0.5 * (front + back), 1e-6);
                var tilt = e.c[0] >= midline ? s.tilt : -s.tilt;
                mats[side].makeTranslation(-e.c[0], -e.c[1], -e.c[2])
                    .premultiply(new THREE.Matrix4().makeRotationZ(-tilt))
                    .premultiply(new THREE.Matrix4().makeTranslation(0, 0, -0.5 * (front - back)))
                    .premultiply(new THREE.Matrix4().makeScale(1 / e.r[1], 1 / e.r[1], 1 / half));
                var p = pupilOffset(s.yaw, s.pitch, aspect, s.pupilRadius, s.maxOffset);
                var ct = Math.cos(-tilt), st = Math.sin(-tilt);
                rows[side].set(ct * p[0] - st * p[1], st * p[0] + ct * p[1], aspect, 1);
            }
            eyeUniforms.eyeLook0.value.w = s.pupilRadius;
            eyeUniforms.eyeLook2.value.w = s.showRegion ? 1 : 0;
            // fill (both eyes), the upper lid's rest (LimitWindow), rim.
            eyeUniforms.eyeLook3.value.set(s.fill, s.lidRest, s.rimWidth, s.rimStrength);
            // Picked in sRGB; the shader mixes linear, as the game's does (EyeRig::Pack).
            eyeUniforms.eyeIris.value.set(srgbToLinear(s.irisColor[0]), srgbToLinear(s.irisColor[1]),
                                          srgbToLinear(s.irisColor[2]), s.irisRadius);

            lids.on = !!s.lidColor;
            lids.rest = s.lidRest;
            lids.lowerRest = s.lowerRest;
            lids.autoBlink = s.autoBlink;
            lids.testClosure = s.testClosure;
            if (s.lidColor)
                eyeUniforms.eyeLid0.value.set(srgbToLinear(s.lidColor[0]), srgbToLinear(s.lidColor[1]),
                                              srgbToLinear(s.lidColor[2]), s.lashWidth);
            eyeUniforms.eyeLid0.value.w = s.lashWidth;
            eyeUniforms.eyeLid1.value.z = s.lashStrength;
            eyeUniforms.eyeExtra.value.set(s.cleanup, s.limitAbove, s.limitBelow, s.lowerRest);
            if (s.lidMR) eyeUniforms.eyeLidMR.value.set(s.lidMR[0], s.lidMR[1], 1, 0);
            else eyeUniforms.eyeLidMR.value.z = 0;
        };

        // Lid colour eyedropper (EyeAnchorEditor.StartLidEyedropper): the next click samples the
        // texture under it.
        var pickMode = false;
        window.setPickMode = function (on) {
            pickMode = on;
            setHint(on ? 'Click the skin next to an eye to take the lid colour.' : null);
        };

        // A click: the lid eyedropper takes the nearest surface's colour; otherwise the selected
        // anchor is centred on the chosen surface (Shift: through glasses).
        function onPick(first, chosen, event) {
            if (pickMode) {
                setPickMode(false);
                try {
                    var mat = materialOf(first);
                    var rgb = sampleTexture(first, mat && mat.map);
                    if (rgb) post({ action: 'colorPicked', rgb: rgb, mr: sampleMR(first) });
                    else showError('No texture under that point to sample.');
                } catch (err) { showError('Could not sample the texture: ' + err.message); }
                return;
            }
            post({ action: 'eyeClicked', p: meshPointFromHit(chosen) });
        }

        function onFrame(dt) { updateLids(dt); }
";
    }
}
