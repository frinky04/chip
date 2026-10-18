namespace Chip.Editor.Rendering;

static class Shaders
{
    public const string MeshVs = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec3 aNrm;
        layout(location=2) in vec2 aUv;
        uniform mat4 uViewProj;
        out vec3 vNrm;
        out vec2 vUv;
        void main() { vNrm = aNrm; vUv = aUv; gl_Position = uViewProj * vec4(aPos, 1.0); }
        """;

    // Per-direction shading keeps flat blockout surfaces readable without real lights.
    public const string MeshFs = """
        #version 330 core
        in vec3 vNrm;
        in vec2 vUv;
        uniform sampler2D uTex;
        out vec4 frag;
        void main() {
            vec3 n = normalize(vNrm);
            float d = dot(n, normalize(vec3(0.35, 1.0, 0.6))) * 0.5 + 0.5;
            vec3 c = texture(uTex, vUv).rgb * (0.48 + 0.52 * d);
            if (!gl_FrontFacing) c = mix(c * 0.35, vec3(0.20, 0.24, 0.32), 0.35); // back faces read as "inside"
            frag = vec4(c, 1.0);
        }
        """;

    // Overlay geometry is pulled slightly toward the eye so it wins depth ties with the surface it sits on.
    public const string ColorVs = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec4 aCol;
        uniform mat4 uViewProj;
        uniform vec3 uEye;
        uniform float uPull;
        uniform float uPointSize;
        out vec4 vCol;
        void main() {
            vec3 p = aPos + (uEye - aPos) * uPull;
            vCol = aCol;
            gl_PointSize = uPointSize;
            gl_Position = uViewProj * vec4(p, 1.0);
        }
        """;

    // Expands each line into a screen-space quad of uWidth pixels (core profile has no wide lines).
    public const string LineGs = """
        #version 330 core
        layout(lines) in;
        layout(triangle_strip, max_vertices = 4) out;
        in vec4 vCol[];
        out vec4 gCol;
        uniform vec2 uViewport;
        uniform float uWidth;
        void main() {
            vec4 a = gl_in[0].gl_Position, b = gl_in[1].gl_Position;
            const float e = 0.01;
            if (a.w < e && b.w < e) return;
            if (a.w < e) a = mix(a, b, (e - a.w) / (b.w - a.w));
            else if (b.w < e) b = mix(b, a, (e - b.w) / (a.w - b.w));
            vec2 hv = uViewport * 0.5;
            vec2 d = b.xy / b.w * hv - a.xy / a.w * hv;
            if (dot(d, d) < 1e-8) d = vec2(1.0, 0.0);
            vec2 n = normalize(vec2(-d.y, d.x)) * uWidth * 0.5 / hv;
            gCol = vCol[0]; gl_Position = a + vec4(n * a.w, 0, 0); EmitVertex();
            gCol = vCol[0]; gl_Position = a - vec4(n * a.w, 0, 0); EmitVertex();
            gCol = vCol[1]; gl_Position = b + vec4(n * b.w, 0, 0); EmitVertex();
            gCol = vCol[1]; gl_Position = b - vec4(n * b.w, 0, 0); EmitVertex();
            EndPrimitive();
        }
        """;

    public const string LineFs = """
        #version 330 core
        in vec4 gCol;
        uniform float uAlpha;
        out vec4 frag;
        void main() { frag = vec4(gCol.rgb, gCol.a * uAlpha); }
        """;

    public const string ColorFs = """
        #version 330 core
        in vec4 vCol;
        uniform float uAlpha;
        out vec4 frag;
        void main() { frag = vec4(vCol.rgb, vCol.a * uAlpha); }
        """;

    public const string GridVs = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        uniform mat4 uViewProj;
        out vec3 vPos;
        void main() { vPos = aPos; gl_Position = uViewProj * vec4(aPos, 1.0); }
        """;

    public const string GridFs = """
        #version 330 core
        in vec3 vPos;
        uniform float uGrid;
        uniform vec3 uEye;
        out vec4 frag;
        float lines(vec2 c, float size) {
            vec2 g = c / size;
            vec2 w = fwidth(g);
            vec2 l = abs(fract(g - 0.5) - 0.5) / w;
            float a = 1.0 - min(min(l.x, l.y), 1.0);
            return a * (1.0 - smoothstep(0.12, 0.4, max(w.x, w.y)));
        }
        void main() {
            vec2 c = vPos.xz;
            float a = max(lines(c, uGrid) * 0.10, lines(c, uGrid * 4.0) * 0.26);
            vec3 col = vec3(0.62);
            vec2 w = fwidth(c) * 1.5;
            float ax = 1.0 - min(abs(c.y) / w.y, 1.0);
            float az = 1.0 - min(abs(c.x) / w.x, 1.0);
            col = mix(col, vec3(0.90, 0.28, 0.30), ax);
            col = mix(col, vec3(0.24, 0.56, 0.93), az);
            a = max(a, max(ax, az) * 0.7);
            a *= 1.0 - smoothstep(100.0, 400.0, length(vPos - uEye));
            if (a < 0.004) discard;
            frag = vec4(col, a);
        }
        """;
}
