import { wsBase, apiToken } from './api';

const VERT = `#version 300 es
in vec2 a_pos;
out vec2 v_uv;
void main() {
  v_uv = vec2((a_pos.x + 1.0) * 0.5, 1.0 - (a_pos.y + 1.0) * 0.5);
  gl_Position = vec4(a_pos, 0.0, 1.0);
}`;

const YUV = `#version 300 es
precision highp float;
in vec2 v_uv;
out vec4 o;
uniform sampler2D u_y;
uniform sampler2D u_u;
uniform sampler2D u_v;
uniform vec2 u_scaleY;
uniform vec2 u_scaleUV;
uniform mat3 u_matrix;
void main() {
  float y = texture(u_y, v_uv * u_scaleY).r;
  float u = texture(u_u, v_uv * u_scaleUV).r;
  float v = texture(u_v, v_uv * u_scaleUV).r;
  vec3 yuv = vec3((y - 0.0625) * 1.1643836, u - 0.5, v - 0.5);
  o = vec4(clamp(u_matrix * yuv, 0.0, 1.0), 1.0);
}`;

const COPY = `#version 300 es
precision highp float;
out vec4 o;
uniform sampler2D u_src;
uniform vec2 u_out;
void main() {
  o = texture(u_src, gl_FragCoord.xy / u_out);
}`;

// AMD FidelityFX Super Resolution 1 (MIT), edge adaptive upscaling. Gather is done with four plain fetches.
const EASU = `#version 300 es
precision highp float;
out vec4 o;
uniform sampler2D u_src;
uniform vec4 u_con0;
uniform vec4 u_con1;
uniform vec4 u_con2;
uniform vec4 u_con3;

vec3 tap(vec2 p) { return textureLod(u_src, p, 0.0).rgb; }

void setF(inout vec2 dir, inout float len, float w, float lA, float lB, float lC, float lD, float lE) {
  float lenX = max(max(abs(lD - lC), abs(lC - lB)), 1e-5);
  float dirX = lD - lB;
  dir.x += dirX * w;
  lenX = clamp(abs(dirX) / lenX, 0.0, 1.0);
  len += lenX * lenX * w;
  float lenY = max(max(abs(lE - lC), abs(lC - lA)), 1e-5);
  float dirY = lE - lA;
  dir.y += dirY * w;
  lenY = clamp(abs(dirY) / lenY, 0.0, 1.0);
  len += lenY * lenY * w;
}

void tapF(inout vec3 aC, inout float aW, vec2 off, vec2 dir, vec2 len, float lob, float clp, vec3 c) {
  vec2 v = vec2(dot(off, dir), dot(off, vec2(-dir.y, dir.x))) * len;
  float d2 = min(dot(v, v), clp);
  float wB = 0.4 * d2 - 1.0;
  float wA = lob * d2 - 1.0;
  wB *= wB;
  wA *= wA;
  wB = 1.5625 * wB - 0.5625;
  float w = wB * wA;
  aC += c * w;
  aW += w;
}

float luma(vec3 c) { return c.g + 0.5 * (c.r + c.b); }

void main() {
  vec2 ip = floor(gl_FragCoord.xy);
  vec2 pp = ip * u_con0.xy + u_con0.zw;
  vec2 fp = floor(pp);
  pp -= fp;
  vec2 p0 = fp * u_con1.xy + u_con1.zw;
  vec2 p1 = p0 + u_con2.xy;
  vec2 p2 = p0 + u_con2.zw;
  vec2 p3 = p0 + u_con3.xy;
  vec4 off = vec4(-0.5, 0.5, -0.5, 0.5) * u_con1.xxyy;

  vec3 bC = tap(p0 + off.xw); float bL = luma(bC);
  vec3 cC = tap(p0 + off.yw); float cL = luma(cC);
  vec3 iC = tap(p1 + off.xw); float iL = luma(iC);
  vec3 jC = tap(p1 + off.yw); float jL = luma(jC);
  vec3 fC = tap(p1 + off.yz); float fL = luma(fC);
  vec3 eC = tap(p1 + off.xz); float eL = luma(eC);
  vec3 kC = tap(p2 + off.xw); float kL = luma(kC);
  vec3 lC = tap(p2 + off.yw); float lL = luma(lC);
  vec3 hC = tap(p2 + off.yz); float hL = luma(hC);
  vec3 gC = tap(p2 + off.xz); float gL = luma(gC);
  vec3 oC = tap(p3 + off.yz); float oL = luma(oC);
  vec3 nC = tap(p3 + off.xz); float nL = luma(nC);

  vec2 dir = vec2(0.0);
  float len = 0.0;
  setF(dir, len, (1.0 - pp.x) * (1.0 - pp.y), bL, eL, fL, gL, jL);
  setF(dir, len, pp.x * (1.0 - pp.y), cL, fL, gL, hL, kL);
  setF(dir, len, (1.0 - pp.x) * pp.y, fL, iL, jL, kL, nL);
  setF(dir, len, pp.x * pp.y, gL, jL, kL, lL, oL);

  vec2 dir2 = dir * dir;
  float dirR = dir2.x + dir2.y;
  bool zro = dirR < 1.0 / 32768.0;
  dirR = zro ? 1.0 : inversesqrt(dirR);
  dir.x = zro ? 1.0 : dir.x;
  dir *= dirR;
  len = len * 0.5;
  len *= len;
  float stretch = dot(dir, dir) / max(abs(dir.x), abs(dir.y));
  vec2 len2 = vec2(1.0 + (stretch - 1.0) * len, 1.0 - 0.5 * len);
  float lob = 0.5 - 0.29 * len;
  float clp = 1.0 / lob;

  vec3 min4 = min(min(fC, gC), min(jC, kC));
  vec3 max4 = max(max(fC, gC), max(jC, kC));
  vec3 aC = vec3(0.0);
  float aW = 0.0;
  tapF(aC, aW, vec2(0.0, -1.0) - pp, dir, len2, lob, clp, bC);
  tapF(aC, aW, vec2(1.0, -1.0) - pp, dir, len2, lob, clp, cC);
  tapF(aC, aW, vec2(-1.0, 1.0) - pp, dir, len2, lob, clp, iC);
  tapF(aC, aW, vec2(0.0, 1.0) - pp, dir, len2, lob, clp, jC);
  tapF(aC, aW, vec2(0.0, 0.0) - pp, dir, len2, lob, clp, fC);
  tapF(aC, aW, vec2(-1.0, 0.0) - pp, dir, len2, lob, clp, eC);
  tapF(aC, aW, vec2(1.0, 1.0) - pp, dir, len2, lob, clp, kC);
  tapF(aC, aW, vec2(2.0, 1.0) - pp, dir, len2, lob, clp, lC);
  tapF(aC, aW, vec2(2.0, 0.0) - pp, dir, len2, lob, clp, hC);
  tapF(aC, aW, vec2(1.0, 0.0) - pp, dir, len2, lob, clp, gC);
  tapF(aC, aW, vec2(1.0, 2.0) - pp, dir, len2, lob, clp, oC);
  tapF(aC, aW, vec2(0.0, 2.0) - pp, dir, len2, lob, clp, nC);
  o = vec4(min(max4, max(min4, aC / aW)), 1.0);
}`;

// FSR 1 robust contrast adaptive sharpening, with its noise detection on so compression noise is left alone.
const RCAS = `#version 300 es
precision highp float;
out vec4 o;
uniform sampler2D u_src;
uniform float u_con;

vec3 at(ivec2 p) {
  ivec2 size = textureSize(u_src, 0);
  return texelFetch(u_src, clamp(p, ivec2(0), size - 1), 0).rgb;
}

void main() {
  ivec2 ip = ivec2(gl_FragCoord.xy);
  vec3 b = at(ip + ivec2(0, -1));
  vec3 d = at(ip + ivec2(-1, 0));
  vec3 e = at(ip);
  vec3 f = at(ip + ivec2(1, 0));
  vec3 h = at(ip + ivec2(0, 1));
  float bL = b.g + 0.5 * (b.b + b.r);
  float dL = d.g + 0.5 * (d.b + d.r);
  float eL = e.g + 0.5 * (e.b + e.r);
  float fL = f.g + 0.5 * (f.b + f.r);
  float hL = h.g + 0.5 * (h.b + h.r);
  float nz = 0.25 * (bL + dL + fL + hL) - eL;
  float range = max(max(max(bL, dL), max(fL, hL)), eL) - min(min(min(bL, dL), min(fL, hL)), eL);
  nz = clamp(abs(nz) / max(range, 1e-5), 0.0, 1.0);
  nz = 1.0 - 0.5 * nz;
  vec3 mn4 = min(min(b, d), min(f, h));
  vec3 mx4 = max(max(b, d), max(f, h));
  vec3 hitMin = mn4 / max(4.0 * mx4, vec3(1e-5));
  vec3 hitMax = (1.0 - mx4) / min(4.0 * mn4 - 4.0, vec3(-1e-5));
  vec3 lobeRGB = max(-hitMin, hitMax);
  float lobe = max(-(0.25 - 1.0 / 16.0), min(max(lobeRGB.r, max(lobeRGB.g, lobeRGB.b)), 0.0)) * u_con;
  lobe *= nz;
  o = vec4((lobe * (b + d + h + f) + e) / (4.0 * lobe + 1.0), 1.0);
}`;

// Column-major BT.601 and BT.709 limited range to RGB.
const BT601 = new Float32Array([1, 1, 1, 0, -0.391762, 2.017232, 1.596027, -0.812968, 0]);
const BT709 = new Float32Array([1, 1, 1, 0, -0.213249, 2.112402, 1.792741, -0.532909, 0]);

// 0 is the strongest; each step of 1 halves the effect.
const SHARPNESS = 0.5;

const ENHANCE_KEY = 'spana.enhanceVideo';
const listeners = new Set<() => void>();

export function enhanceEnabled() {
  try {
    return (localStorage.getItem(ENHANCE_KEY) ?? localStorage.getItem('setv.enhanceVideo')) !== '0';
  } catch {
    return true;
  }
}

export function subscribeEnhance(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function setEnhanceEnabled(on: boolean) {
  try {
    localStorage.setItem(ENHANCE_KEY, on ? '1' : '0');
  } catch {
    /* storage unavailable */
  }
  listeners.forEach((l) => l());
}

export interface FrameInfo {
  width: number;
  height: number;
  aspect: number;
}

type Program = { program: WebGLProgram; u: Record<string, WebGLUniformLocation | null> };
type Target = { tex: WebGLTexture; fb: WebGLFramebuffer; w: number; h: number };

export class VideoRenderer {
  private gl: WebGL2RenderingContext;
  private planes: WebGLTexture[] = [];
  private yuv!: Program;
  private copy!: Program;
  private easu!: Program;
  private rcas!: Program;
  private source: Target | null = null;
  private scaled: Target | null = null;
  private hasFrame = false;
  private socket: WebSocket | null = null;
  private latest: ArrayBuffer | null = null;
  private raf = 0;
  private texW = 0;
  private texH = 0;
  private closed = false;
  private retry: number | undefined;
  private resize: ResizeObserver;
  private enhance = enhanceEnabled();
  private onSetting = () => {
    this.enhance = enhanceEnabled();
    this.present();
  };
  onFrameInfo?: (info: FrameInfo) => void;
  private lastInfo = '';

  constructor(private canvas: HTMLCanvasElement) {
    const gl = canvas.getContext('webgl2', {
      alpha: false,
      antialias: false,
      depth: false,
      preserveDrawingBuffer: false,
      powerPreference: 'high-performance',
    });
    if (!gl) throw new Error('WebGL 2 is not available');
    this.gl = gl;
    this.setup();
    this.connect();
    listeners.add(this.onSetting);

    // The canvas is drawn at the real screen resolution, so scaling happens in our shaders instead of the browser.
    this.resize = new ResizeObserver((entries) => {
      const entry = entries[0];
      const box = entry.devicePixelContentBoxSize?.[0];
      const w = Math.max(1, Math.round(box ? box.inlineSize : entry.contentRect.width * devicePixelRatio));
      const h = Math.max(1, Math.round(box ? box.blockSize : entry.contentRect.height * devicePixelRatio));
      if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w;
        canvas.height = h;
        this.present();
      }
    });
    try {
      this.resize.observe(canvas, { box: 'device-pixel-content-box' });
    } catch {
      this.resize.observe(canvas);
    }
    this.raf = requestAnimationFrame(this.tick);
  }

  private setup() {
    const gl = this.gl;
    const compile = (type: number, src: string) => {
      const s = gl.createShader(type)!;
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s) ?? '');
      return s;
    };
    const vert = compile(gl.VERTEX_SHADER, VERT);
    const link = (frag: string, uniforms: string[]): Program => {
      const program = gl.createProgram()!;
      gl.attachShader(program, vert);
      gl.attachShader(program, compile(gl.FRAGMENT_SHADER, frag));
      gl.bindAttribLocation(program, 0, 'a_pos');
      gl.linkProgram(program);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? '');
      const u: Program['u'] = {};
      for (const name of uniforms) u[name] = gl.getUniformLocation(program, name);
      return { program, u };
    };
    this.yuv = link(YUV, ['u_y', 'u_u', 'u_v', 'u_scaleY', 'u_scaleUV', 'u_matrix']);
    this.copy = link(COPY, ['u_src', 'u_out']);
    this.easu = link(EASU, ['u_src', 'u_con0', 'u_con1', 'u_con2', 'u_con3']);
    this.rcas = link(RCAS, ['u_src', 'u_con']);

    const buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

    gl.useProgram(this.yuv.program);
    for (const [i, name] of ['u_y', 'u_u', 'u_v'].entries()) {
      const tex = gl.createTexture()!;
      gl.activeTexture(gl.TEXTURE0 + i);
      gl.bindTexture(gl.TEXTURE_2D, tex);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.uniform1i(this.yuv.u[name], i);
      this.planes.push(tex);
    }
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
  }

  private target(current: Target | null, w: number, h: number, mipmaps: boolean): Target {
    const gl = this.gl;
    if (current && current.w === w && current.h === h) return current;
    if (current) {
      gl.deleteTexture(current.tex);
      gl.deleteFramebuffer(current.fb);
    }
    const tex = gl.createTexture()!;
    gl.activeTexture(gl.TEXTURE3);
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texStorage2D(gl.TEXTURE_2D, mipmaps ? Math.floor(Math.log2(Math.max(w, h))) + 1 : 1, gl.RGBA8, w, h);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    const fb = gl.createFramebuffer()!;
    gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
    return { tex, fb, w, h };
  }

  private connect() {
    const socket = new WebSocket(`${wsBase}/video?t=${apiToken}`);
    socket.binaryType = 'arraybuffer';
    socket.onmessage = (e) => {
      this.latest = e.data as ArrayBuffer;
    };
    socket.onclose = () => {
      if (!this.closed) this.retry = window.setTimeout(() => this.connect(), 1000);
    };
    this.socket = socket;
  }

  private tick = () => {
    this.raf = requestAnimationFrame(this.tick);
    const frame = this.latest;
    if (!frame) return;
    this.latest = null;
    this.draw(frame);
  };

  private draw(frame: ArrayBuffer) {
    const gl = this.gl;
    const h32 = new Int32Array(frame, 0, 10);
    const width = h32[1];
    const height = h32[2];
    const yPitch = h32[3];
    const uvPitch = h32[4];
    const sarNum = h32[5] || 1;
    const sarDen = h32[6] || 1;
    const visW = h32[8] || width;
    const visH = h32[9] || height;
    const ySize = yPitch * height;
    const uvSize = uvPitch * (height / 2);
    const data = new Uint8Array(frame, 40);

    const resized = this.texW !== yPitch || this.texH !== height;
    const planes: [Uint8Array, number, number][] = [
      [data.subarray(0, ySize), yPitch, height],
      [data.subarray(ySize, ySize + uvSize), uvPitch, height / 2],
      [data.subarray(ySize + uvSize, ySize + uvSize * 2), uvPitch, height / 2],
    ];
    planes.forEach(([plane, w, h], i) => {
      gl.activeTexture(gl.TEXTURE0 + i);
      gl.bindTexture(gl.TEXTURE_2D, this.planes[i]);
      if (resized) gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, w, h, 0, gl.RED, gl.UNSIGNED_BYTE, plane);
      else gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, w, h, gl.RED, gl.UNSIGNED_BYTE, plane);
    });
    this.texW = yPitch;
    this.texH = height;

    this.source = this.target(this.source, visW, visH, true);
    gl.bindFramebuffer(gl.FRAMEBUFFER, this.source.fb);
    gl.viewport(0, 0, visW, visH);
    gl.useProgram(this.yuv.program);
    gl.uniform2f(this.yuv.u.u_scaleY, visW / yPitch, visH / height);
    gl.uniform2f(this.yuv.u.u_scaleUV, visW / 2 / uvPitch, visH / height);
    gl.uniformMatrix3fv(this.yuv.u.u_matrix, false, height >= 720 ? BT709 : BT601);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    this.hasFrame = true;
    this.present();

    const aspect = (visW * sarNum) / (visH * sarDen);
    const key = `${visW}x${visH}@${aspect.toFixed(3)}`;
    if (key !== this.lastInfo) {
      this.lastInfo = key;
      this.onFrameInfo?.({ width: visW, height: visH, aspect });
    }
  }

  // Scales the converted frame to the canvas: FSR 1 when enlarging, mipmapped filtering when shrinking.
  private present() {
    const gl = this.gl;
    const src = this.source;
    if (!src || !this.hasFrame) return;
    const outW = this.canvas.width;
    const outH = this.canvas.height;
    const upscale = outW >= src.w * 0.99 && outH >= src.h * 0.99;

    if (this.enhance && upscale) {
      this.scaled = this.target(this.scaled, outW, outH, false);
      gl.bindFramebuffer(gl.FRAMEBUFFER, this.scaled.fb);
      gl.viewport(0, 0, outW, outH);
      gl.useProgram(this.easu.program);
      gl.activeTexture(gl.TEXTURE3);
      gl.bindTexture(gl.TEXTURE_2D, src.tex);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.uniform1i(this.easu.u.u_src, 3);
      gl.uniform4f(this.easu.u.u_con0, src.w / outW, src.h / outH, (0.5 * src.w) / outW - 0.5, (0.5 * src.h) / outH - 0.5);
      gl.uniform4f(this.easu.u.u_con1, 1 / src.w, 1 / src.h, 1 / src.w, -1 / src.h);
      gl.uniform4f(this.easu.u.u_con2, -1 / src.w, 2 / src.h, 1 / src.w, 2 / src.h);
      gl.uniform4f(this.easu.u.u_con3, 0, 4 / src.h, 0, 0);
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);

      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      gl.viewport(0, 0, outW, outH);
      gl.useProgram(this.rcas.program);
      gl.bindTexture(gl.TEXTURE_2D, this.scaled.tex);
      gl.uniform1i(this.rcas.u.u_src, 3);
      gl.uniform1f(this.rcas.u.u_con, Math.pow(2, -SHARPNESS));
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      return;
    }

    gl.activeTexture(gl.TEXTURE3);
    gl.bindTexture(gl.TEXTURE_2D, src.tex);
    if (!upscale) {
      gl.generateMipmap(gl.TEXTURE_2D);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    } else {
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, outW, outH);
    gl.useProgram(this.copy.program);
    gl.uniform1i(this.copy.u.u_src, 3);
    gl.uniform2f(this.copy.u.u_out, outW, outH);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  }

  clear() {
    const gl = this.gl;
    this.latest = null;
    this.hasFrame = false;
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    gl.clearColor(0, 0, 0, 1);
    gl.clear(gl.COLOR_BUFFER_BIT);
    this.lastInfo = '';
  }

  dispose() {
    this.closed = true;
    listeners.delete(this.onSetting);
    this.resize.disconnect();
    cancelAnimationFrame(this.raf);
    window.clearTimeout(this.retry);
    this.socket?.close();
  }
}
