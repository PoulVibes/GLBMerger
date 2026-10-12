namespace GlbMerger
{
    // EyeAnchorEditor's three.js preview. The model's materials are patched with a copy of the
    // game's eye overlay (FootballRoguelite shaders/EyeOverlay.glsl + model.vert's eye lines and
    // EyeRig::PupilOffset) - keep the two in step, or what is tuned here stops being what the game
    // draws. Everything runs in the mesh's raw bind-pose POSITION space, the same space the anchors
    // are authored in, so the overlay sticks to the face however the rig is posed.
    public partial class EyeAnchorEditor
    {
        private static string BuildViewerHtml(string previewFileName) => @"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js'></script>
    <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js'></script>
    <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/controls/OrbitControls.js'></script>
    <style>
        body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #23272a; }
        #viewport { width: 100%; height: 100%; display: block; }
        #error-overlay {
            position: absolute; top: 10px; left: 10px; right: 10px;
            background: rgba(139, 0, 0, 0.92); color: #fff; padding: 12px;
            border-radius: 6px; font-family: monospace; font-size: 12px;
            white-space: pre-wrap; display: none; max-height: 40%; overflow: auto;
        }
        #hint { position: absolute; bottom: 8px; left: 10px; color: #aaa; font: 12px sans-serif; }
    </style>
</head>
<body>
    <canvas id='viewport'></canvas>
    <div id='error-overlay'></div>
    <div id='hint'>Click an eye to place the selected anchor. Drag to orbit, wheel to zoom.</div>
    <script>
        function showError(msg) {
            var el = document.querySelector('#error-overlay');
            el.style.display = 'block';
            el.textContent += msg + '\n';
        }
        window.onerror = function (message) { showError('JS error: ' + message); };
        function post(obj) { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(JSON.stringify(obj)); }

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

        function srgbToLinear(c) { return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); }

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
        // texture under it. The GLB's images load as blob URLs, so the canvas isn't tainted.
        var pickMode = false;
        window.setPickMode = function (on) {
            pickMode = on;
            document.querySelector('#hint').textContent = on
                ? 'Click the skin next to an eye to take the lid colour.'
                : 'Click an eye to place the selected anchor. Drag to orbit, wheel to zoom.';
        };
        var sampleCanvases = new Map();
        function materialOf(hit) {
            return Array.isArray(hit.object.material) ? hit.object.material[hit.face.materialIndex] : hit.object.material;
        }

        // The lid skin's roughness / metalness under the click: the material's factors times its
        // metallicRoughness texture (glTF: G = roughness, B = metalness), if it has one.
        function sampleMR(hit) {
            var mat = materialOf(hit);
            if (!mat || mat.roughness === undefined) return null;
            var tex = mat.roughnessMap || mat.metalnessMap;
            var t = tex ? sampleTexture(hit, tex) : null;
            return [mat.roughness * (t ? t[1] : 1), mat.metalness * (t ? t[2] : 1)];
        }

        function sampleTexture(hit, map) {
            if (!map || !map.image || !hit.uv) return null;
            var img = map.image, cv = sampleCanvases.get(img);
            if (!cv) {
                cv = document.createElement('canvas');
                cv.width = img.width; cv.height = img.height;
                cv.getContext('2d').drawImage(img, 0, 0);
                sampleCanvases.set(img, cv);
            }
            var uv = hit.uv.clone();
            map.transformUv(uv);
            var x = Math.min(Math.max(Math.floor(uv.x * cv.width) - 1, 0), cv.width - 3);
            var y = Math.min(Math.max(Math.floor(uv.y * cv.height) - 1, 0), cv.height - 3);
            var d = cv.getContext('2d').getImageData(x, y, 3, 3).data, rgb = [0, 0, 0];
            for (var i = 0; i < 9; i++) { rgb[0] += d[i * 4]; rgb[1] += d[i * 4 + 1]; rgb[2] += d[i * 4 + 2]; }
            return [rgb[0] / 2295, rgb[1] / 2295, rgb[2] / 2295];   // 9 texels x 255
        }

        try {
            var canvas = document.querySelector('#viewport');
            var renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true });
            renderer.setPixelRatio(window.devicePixelRatio);
            renderer.setSize(window.innerWidth, window.innerHeight);
            renderer.outputEncoding = THREE.sRGBEncoding;

            var scene = new THREE.Scene();
            scene.background = new THREE.Color(0x1a1c1e);
            var camera = new THREE.PerspectiveCamera(35, window.innerWidth / window.innerHeight, 0.001, 10000);
            var controls = new THREE.OrbitControls(camera, renderer.domElement);
            controls.enableDamping = true;

            scene.add(new THREE.HemisphereLight(0xffffff, 0x444444, 1.2));
            scene.add(new THREE.AmbientLight(0xffffff, 0.6));
            var dirLight = new THREE.DirectionalLight(0xffffff, 1.2);
            dirLight.position.set(2, 5, 7.5);
            scene.add(dirLight);

            var meshes = [];
            var headBone = null;
            var modelHeight = 1;

            function normalise(name) {
                var s = (name || '').toLowerCase().replace(/[^a-z0-9]/g, '');
                return s.indexOf('mixamorig') === 0 ? s.substring(9) : s;
            }

            window.frameHead = function () {
                if (meshes.length === 0) return;
                var target = new THREE.Vector3();
                if (headBone) headBone.getWorldPosition(target);
                else new THREE.Box3().setFromObject(scene).getCenter(target);
                var forward = new THREE.Vector3(0, 0, 1).transformDirection(meshes[0].matrixWorld);
                controls.target.copy(target);
                camera.position.copy(target).addScaledVector(forward, modelHeight * 0.45);
                controls.update();
            };

            // The clicked point back in raw POSITION space: barycentric weights on the triangle
            // as drawn (skinned), applied to its bind-pose corners - right whether or not the
            // rest pose matches the bind pose.
            function meshPointFromHit(hit) {
                var mesh = hit.object, pos = mesh.geometry.attributes.position, f = hit.face;
                var ids = [f.a, f.b, f.c], drawn = [];
                for (var k = 0; k < 3; k++) {
                    var v = new THREE.Vector3();
                    if (mesh.isSkinnedMesh) mesh.boneTransform(ids[k], v);
                    else v.fromBufferAttribute(pos, ids[k]);
                    drawn.push(v.applyMatrix4(mesh.matrixWorld));
                }
                var bary = new THREE.Vector3();
                THREE.Triangle.getBarycoord(hit.point, drawn[0], drawn[1], drawn[2], bary);
                var w = [bary.x, bary.y, bary.z], out = [0, 0, 0];
                for (var k = 0; k < 3; k++) {
                    out[0] += w[k] * pos.getX(ids[k]);
                    out[1] += w[k] * pos.getY(ids[k]);
                    out[2] += w[k] * pos.getZ(ids[k]);
                }
                return out;
            }

            var loader = new THREE.GLTFLoader();
            loader.load('https://appassets.local/" + previewFileName + @"', function (gltf) {
                scene.add(gltf.scene);
                gltf.scene.traverse(function (obj) {
                    if (obj.isMesh) {
                        meshes.push(obj);
                        var mats = Array.isArray(obj.material) ? obj.material : [obj.material];
                        mats.forEach(patchMaterial);
                    }
                    if (obj.isBone && !headBone && normalise(obj.name) === 'head') headBone = obj;
                });
                gltf.scene.updateMatrixWorld(true);
                var box = new THREE.Box3().setFromObject(gltf.scene);
                modelHeight = Math.max(box.max.y - box.min.y, 1e-3);
                camera.near = modelHeight * 0.001;
                camera.far = modelHeight * 100;
                camera.updateProjectionMatrix();
                frameHead();
                post({ action: 'loaded' });
            }, undefined, function (error) {
                showError('Failed to load preview: ' + (error && error.message ? error.message : error));
            });

            window.addEventListener('resize', function () {
                camera.aspect = window.innerWidth / window.innerHeight;
                camera.updateProjectionMatrix();
                renderer.setSize(window.innerWidth, window.innerHeight);
            });

            // A press that moved more than a few pixels was an orbit drag, not a click.
            var raycaster = new THREE.Raycaster();
            var pickVec = new THREE.Vector2();
            var pressX = 0, pressY = 0;
            canvas.addEventListener('pointerdown', function (event) { pressX = event.clientX; pressY = event.clientY; });
            canvas.addEventListener('click', function (event) {
                if (Math.abs(event.clientX - pressX) > 4 || Math.abs(event.clientY - pressY) > 4) return;
                var rect = canvas.getBoundingClientRect();
                pickVec.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
                pickVec.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
                raycaster.setFromCamera(pickVec, camera);
                var hits = raycaster.intersectObjects(meshes, false);
                if (hits.length === 0 || !hits[0].face) return;
                if (pickMode) {
                    setPickMode(false);
                    try {
                        var mat = materialOf(hits[0]);
                        var rgb = sampleTexture(hits[0], mat && mat.map);
                        if (rgb) post({ action: 'colorPicked', rgb: rgb, mr: sampleMR(hits[0]) });
                        else showError('No texture under that point to sample.');
                    } catch (err) { showError('Could not sample the texture: ' + err.message); }
                    return;
                }
                // Shift: through the first surface (glasses) to the next one behind it.
                var hit = hits[0];
                if (event.shiftKey)
                    for (var h = 1; h < hits.length; h++)
                        if (hits[h].face && hits[h].distance > hits[0].distance + 1e-4 * modelHeight) { hit = hits[h]; break; }
                post({ action: 'eyeClicked', p: meshPointFromHit(hit) });
            });

            var lidClock = new THREE.Clock();
            (function animate() {
                requestAnimationFrame(animate);
                updateLids(Math.min(lidClock.getDelta(), 0.1));
                controls.update();
                renderer.render(scene, camera);
            })();
        } catch (err) {
            showError('Setup error: ' + err.message);
        }
    </script>
</body>
</html>";
    }
}
