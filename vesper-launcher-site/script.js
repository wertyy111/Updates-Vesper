const body = document.body;
const topbar = document.querySelector(".topbar");
const menuToggle = document.querySelector(".menu-toggle");
const panels = Array.from(document.querySelectorAll(".window-panel"));
const navLinks = Array.from(document.querySelectorAll(".nav-pill a[data-window-target]"));
const triggers = Array.from(document.querySelectorAll("[data-window-target]"));
const panelNames = new Set(panels.map((panel) => panel.dataset.window));
const siteMesh = document.querySelector("[data-mesh]");
const prefersReducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
const prefersMobileBackground = window.matchMedia("(max-width: 900px)");

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
const snapToDevicePixel = (value) => {
  const scale = window.devicePixelRatio || 1;
  return Math.round(value * scale) / scale;
};

const pointerState = {
  currentX: 0.72,
  currentY: 0.32,
  previousX: 0.72,
  previousY: 0.32,
  targetX: 0.72,
  targetY: 0.32,
  rafId: 0,
};

function syncPointerGlow(force = false) {
  const easing = force ? 1 : 0.12;

  pointerState.currentX += (pointerState.targetX - pointerState.currentX) * easing;
  pointerState.currentY += (pointerState.targetY - pointerState.currentY) * easing;

  body.style.setProperty("--pointer-x", `${(pointerState.currentX * 100).toFixed(2)}%`);
  body.style.setProperty("--pointer-y", `${(pointerState.currentY * 100).toFixed(2)}%`);
}

function stepPointerGlow() {
  syncPointerGlow();

  const dx = Math.abs(pointerState.targetX - pointerState.currentX);
  const dy = Math.abs(pointerState.targetY - pointerState.currentY);

  if (dx < 0.0005 && dy < 0.0005) {
    pointerState.rafId = 0;
    return;
  }

  pointerState.rafId = window.requestAnimationFrame(stepPointerGlow);
}

function queuePointerGlow() {
  if (pointerState.rafId) {
    return;
  }

  pointerState.rafId = window.requestAnimationFrame(stepPointerGlow);
}

function setPointerTarget(clientX, clientY) {
  pointerState.targetX = clamp(clientX / window.innerWidth, 0, 1);
  pointerState.targetY = clamp(clientY / window.innerHeight, 0, 1);
  queuePointerGlow();
}

function resetPointerTarget() {
  pointerState.targetX = 0.72;
  pointerState.targetY = 0.32;
  queuePointerGlow();
}

function getWindowFromHash(hash = window.location.hash) {
  const name = hash.replace(/^#/, "").trim();
  return panelNames.has(name) ? name : "home";
}

function closeMobileMenu() {
  if (!topbar || !menuToggle) {
    return;
  }

  topbar.classList.remove("menu-open");
  menuToggle.setAttribute("aria-expanded", "false");
}

let navIndicatorReady = false;
let navIndicatorFrame = 0;

function ensureNavIndicator(nav) {
  let indicator = nav.querySelector(".nav-active-indicator");

  if (!indicator) {
    indicator = document.createElement("span");
    indicator.className = "nav-active-indicator";
    indicator.setAttribute("aria-hidden", "true");
    nav.prepend(indicator);
  }

  return indicator;
}

function syncNavIndicator(options = {}) {
  const nav = document.querySelector(".nav-pill");
  const activeLink = nav?.querySelector('a.is-active, a[aria-current="page"]');

  if (!nav || !activeLink || window.innerWidth <= 900) {
    nav?.classList.remove("is-nav-ready");
    nav?.style.setProperty("--nav-active-opacity", "0");
    return;
  }

  ensureNavIndicator(nav);

  const navRect = nav.getBoundingClientRect();
  const linkRect = activeLink.getBoundingClientRect();
  const instant = options.instant || !navIndicatorReady || prefersReducedMotion.matches;

  if (navIndicatorFrame) {
    window.cancelAnimationFrame(navIndicatorFrame);
  }

  nav.classList.toggle("nav-indicator-no-motion", instant);
  nav.style.setProperty("--nav-active-x", `${snapToDevicePixel(linkRect.left - navRect.left - nav.clientLeft)}px`);
  nav.style.setProperty("--nav-active-y", `${snapToDevicePixel(linkRect.top - navRect.top - nav.clientTop)}px`);
  nav.style.setProperty("--nav-active-w", `${snapToDevicePixel(linkRect.width)}px`);
  nav.style.setProperty("--nav-active-h", `${snapToDevicePixel(linkRect.height)}px`);
  nav.style.setProperty("--nav-active-opacity", "1");

  navIndicatorFrame = window.requestAnimationFrame(() => {
    nav.classList.add("is-nav-ready");
    navIndicatorReady = true;

    if (instant) {
      navIndicatorFrame = window.requestAnimationFrame(() => {
        nav.classList.remove("nav-indicator-no-motion");
        navIndicatorFrame = 0;
      });
      return;
    }

    navIndicatorFrame = 0;
  });
}

function activateWindow(targetWindow) {
  const nextWindow = panelNames.has(targetWindow) ? targetWindow : "home";
  const currentWindow = document.querySelector(".window-panel.is-active")?.dataset.window;

  if (currentWindow === nextWindow) {
    window.requestAnimationFrame(syncNavIndicator);
    closeMobileMenu();
    return;
  }

  panels.forEach((panel) => {
    const isActive = panel.dataset.window === nextWindow;
    panel.classList.toggle("is-active", isActive);
    panel.hidden = !isActive;

    if (isActive) {
      panel.removeAttribute("inert");
    } else {
      panel.setAttribute("inert", "");
    }
  });

  navLinks.forEach((link) => {
    const isActive = link.dataset.windowTarget === nextWindow;
    link.classList.toggle("is-active", isActive);

    if (isActive) {
      link.setAttribute("aria-current", "page");
    } else {
      link.removeAttribute("aria-current");
    }
  });

  window.requestAnimationFrame(syncNavIndicator);

  closeMobileMenu();
}

function createBackgroundRenderer(canvas) {
  const gl = canvas.getContext("webgl", {
    alpha: true,
    antialias: true,
    premultipliedAlpha: false,
    powerPreference: "high-performance",
  });

  if (!gl) {
    return null;
  }

  const vertexShaderSource = `
    attribute vec2 aPosition;
    varying vec2 vUv;

    void main() {
      vUv = aPosition * 0.5 + 0.5;
      gl_Position = vec4(aPosition, 0.0, 1.0);
    }
  `;

  const fragmentShaderSource = `
    precision highp float;

    varying vec2 vUv;

    uniform vec2 uResolution;
    uniform float uTime;
    uniform vec2 uPointer;
    uniform vec2 uPointerPrev;
    uniform float uMotion;

    float hash12(vec2 p) {
      vec3 p3 = fract(vec3(p.xyx) * 0.1031);
      p3 += dot(p3, p3.yzx + 33.33);
      return fract((p3.x + p3.y) * p3.z);
    }

    mat2 rot(float a) {
      float s = sin(a);
      float c = cos(a);
      return mat2(c, -s, s, c);
    }

    float noise(vec2 p) {
      vec2 i = floor(p);
      vec2 f = fract(p);
      vec2 u = f * f * (3.0 - 2.0 * f);

      float a = hash12(i);
      float b = hash12(i + vec2(1.0, 0.0));
      float c = hash12(i + vec2(0.0, 1.0));
      float d = hash12(i + vec2(1.0, 1.0));

      return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
    }

    float fbm(vec2 p) {
      float value = 0.0;
      float amplitude = 0.5;

      for (int i = 0; i < 5; i++) {
        value += amplitude * noise(p);
        p = rot(0.52) * p * 2.04 + vec2(0.37, -0.28);
        amplitude *= 0.54;
      }

      return value;
    }

    float bayer4(vec2 fragCoord) {
      vec2 cell = mod(floor(fragCoord), 4.0);
      float index = cell.x + cell.y * 4.0;

      if (index < 0.5) return 0.0 / 16.0;
      if (index < 1.5) return 8.0 / 16.0;
      if (index < 2.5) return 2.0 / 16.0;
      if (index < 3.5) return 10.0 / 16.0;
      if (index < 4.5) return 12.0 / 16.0;
      if (index < 5.5) return 4.0 / 16.0;
      if (index < 6.5) return 14.0 / 16.0;
      if (index < 7.5) return 6.0 / 16.0;
      if (index < 8.5) return 3.0 / 16.0;
      if (index < 9.5) return 11.0 / 16.0;
      if (index < 10.5) return 1.0 / 16.0;
      if (index < 11.5) return 9.0 / 16.0;
      if (index < 12.5) return 15.0 / 16.0;
      if (index < 13.5) return 7.0 / 16.0;
      if (index < 14.5) return 13.0 / 16.0;
      return 5.0 / 16.0;
    }

    float segmentDistance(vec2 p, vec2 a, vec2 b) {
      vec2 pa = p - a;
      vec2 ba = b - a;
      float h = clamp(dot(pa, ba) / max(dot(ba, ba), 0.0001), 0.0, 1.0);
      return length(pa - ba * h);
    }

    vec3 applyPalette(float mask, float bloom, float beam, float edge, float sparkle) {
      vec3 base = vec3(0.005, 0.007, 0.02);
      vec3 low = vec3(0.026, 0.04, 0.12);
      vec3 mid = vec3(0.13, 0.2, 0.62);
      vec3 high = vec3(0.43, 0.56, 1.0);

      vec3 color = mix(base, low, clamp(mask * 1.2, 0.0, 1.0));
      color = mix(color, mid, clamp(mask * 0.92 + beam * 0.24, 0.0, 1.0));
      color += high * bloom;
      color += vec3(0.22, 0.28, 0.72) * beam;
      color += vec3(0.1, 0.14, 0.4) * edge;
      color += vec3(0.16, 0.2, 0.5) * sparkle;
      return color;
    }

    void main() {
      vec2 fragCoord = gl_FragCoord.xy;
      vec2 centered = (fragCoord - 0.5 * uResolution.xy) / uResolution.y;
      vec2 aspect = vec2(uResolution.x / uResolution.y, 1.0);

      float time = uTime;
      vec2 pointerNorm = vec2(uPointer.x, 1.0 - uPointer.y);
      vec2 pointerPrevNorm = vec2(uPointerPrev.x, 1.0 - uPointerPrev.y);
      vec2 pointer = (pointerNorm - 0.5) * aspect;
      vec2 pointerPrev = (pointerPrevNorm - 0.5) * aspect;

      vec2 fieldUv = centered;
      fieldUv *= rot(0.11);

      float macro = fbm(fieldUv * vec2(2.0, 1.6) + vec2(time * 0.025, -time * 0.012));
      float ribbon = fbm((fieldUv + vec2(macro * 0.45, 0.0)) * vec2(4.5, 3.2) - vec2(time * 0.06, -time * 0.035));
      float detail = fbm(fieldUv * 9.0 + ribbon * 1.8 + vec2(-time * 0.08, time * 0.05));

      vec2 bloomCenterA = vec2(0.64 * aspect.x, -0.02);
      vec2 bloomCenterB = vec2(-0.82 * aspect.x, -0.44);
      vec2 bloomCenterC = vec2(0.26 * aspect.x, 0.32);

      float bloomA = exp(-length(centered - bloomCenterA) * 3.4) * (0.74 + ribbon * 0.52);
      float bloomB = exp(-length(centered - bloomCenterB) * 3.0) * (0.32 + macro * 0.26);
      float bloomC = exp(-length(centered - bloomCenterC) * 4.6) * (0.22 + detail * 0.24);

      float beam = exp(-abs(centered.y + centered.x * 0.18 - 0.08) * 10.0) * 0.22;
      beam += exp(-abs(centered.x - 0.28 * aspect.x) * 12.0) * 0.06;

      float pointerGlow = exp(-length(centered - pointer) * 5.6) * 0.56 * uMotion;
      float pointerSpread = exp(-length(centered - pointer) * 2.6) * 0.22 * uMotion;
      float trail = exp(-segmentDistance(centered, pointerPrev, pointer) * 18.0) * 0.76 * uMotion;
      trail *= smoothstep(0.01, 0.16, length(pointer - pointerPrev) + 0.016);

      float field = macro * 0.34 + ribbon * 0.28 + detail * 0.18;
      field += bloomA * 0.84 + bloomB * 0.34 + bloomC * 0.2;
      field += beam * 0.34 + pointerGlow + pointerSpread + trail;

      vec2 coarseCoord = floor(fragCoord * 0.5);
      float dither = bayer4(coarseCoord) - 0.5;
      float grain = hash12(coarseCoord + floor(time * 48.0)) - 0.5;
      float sparkle = smoothstep(0.92, 1.0, hash12(floor(fragCoord * 0.35) + floor(time * 1.8)));
      float stepped = floor(clamp(field + dither * 0.2 + grain * 0.03, 0.0, 1.0) * 20.0) / 20.0;

      float edgeNoise = fbm(centered * 14.0 + vec2(time * 0.04, -time * 0.03));
      float edge = smoothstep(0.52, 0.88, stepped + edgeNoise * 0.12);
      float vignette = smoothstep(1.56, 0.14, length(centered * vec2(0.9, 0.96)));

      float bloom = clamp(bloomA * 0.8 + pointerGlow * 0.56 + trail * 0.72, 0.0, 1.0);
      vec3 color = applyPalette(stepped, bloom, beam, edge * 0.08, sparkle * 0.035);

      color *= vignette;
      color += vec3(0.01, 0.014, 0.04) * (1.0 - vignette);

      float alpha = clamp(stepped * 0.92 + bloom * 0.54 + bloomB * 0.2 + beam * 0.16, 0.0, 0.94);

      gl_FragColor = vec4(color, alpha);
    }
  `;

  function compileShader(type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);

    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      const info = gl.getShaderInfoLog(shader);
      gl.deleteShader(shader);
      throw new Error(info || "Shader compile failed");
    }

    return shader;
  }

  function createProgram(vertexSource, fragmentSource) {
    const program = gl.createProgram();
    const vertexShader = compileShader(gl.VERTEX_SHADER, vertexSource);
    const fragmentShader = compileShader(gl.FRAGMENT_SHADER, fragmentSource);

    gl.attachShader(program, vertexShader);
    gl.attachShader(program, fragmentShader);
    gl.linkProgram(program);

    gl.deleteShader(vertexShader);
    gl.deleteShader(fragmentShader);

    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
      const info = gl.getProgramInfoLog(program);
      gl.deleteProgram(program);
      throw new Error(info || "Program link failed");
    }

    return program;
  }

  let program;

  try {
    program = createProgram(vertexShaderSource, fragmentShaderSource);
  } catch (error) {
    console.error("WebGL background init failed:", error);
    return null;
  }

  const buffer = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);

  const positionLocation = gl.getAttribLocation(program, "aPosition");
  const resolutionLocation = gl.getUniformLocation(program, "uResolution");
  const timeLocation = gl.getUniformLocation(program, "uTime");
  const pointerLocation = gl.getUniformLocation(program, "uPointer");
  const pointerPrevLocation = gl.getUniformLocation(program, "uPointerPrev");
  const motionLocation = gl.getUniformLocation(program, "uMotion");

  let rafId = 0;
  let width = 0;
  let height = 0;
  let reducedMotion = prefersReducedMotion.matches;
  let renderTime = 0;

  function resize() {
    const dpr = Math.min(window.devicePixelRatio || 1, 1.75);
    const nextWidth = Math.max(1, Math.round(window.innerWidth * dpr));
    const nextHeight = Math.max(1, Math.round(window.innerHeight * dpr));

    if (nextWidth === width && nextHeight === height) {
      return;
    }

    width = nextWidth;
    height = nextHeight;

    canvas.width = width;
    canvas.height = height;
    gl.viewport(0, 0, width, height);
  }

  function drawFrame(now) {
    renderTime = now * 0.001;

    syncPointerGlow();

    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.useProgram(program);
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.enableVertexAttribArray(positionLocation);
    gl.vertexAttribPointer(positionLocation, 2, gl.FLOAT, false, 0, 0);
    gl.uniform2f(resolutionLocation, width, height);
    gl.uniform1f(timeLocation, reducedMotion ? 4.2 : renderTime);
    gl.uniform2f(pointerLocation, pointerState.currentX, pointerState.currentY);
    gl.uniform2f(pointerPrevLocation, pointerState.previousX, pointerState.previousY);
    gl.uniform1f(motionLocation, reducedMotion || prefersMobileBackground.matches ? 0 : 1);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);

    pointerState.previousX += (pointerState.currentX - pointerState.previousX) * 0.18;
    pointerState.previousY += (pointerState.currentY - pointerState.previousY) * 0.18;

    if (!reducedMotion && !document.hidden) {
      rafId = window.requestAnimationFrame(drawFrame);
    } else {
      rafId = 0;
    }
  }

  function start() {
    if (rafId) {
      return;
    }

    resize();
    rafId = window.requestAnimationFrame(drawFrame);
  }

  function stop() {
    if (!rafId) {
      return;
    }

    window.cancelAnimationFrame(rafId);
    rafId = 0;
  }

  function renderStill() {
    resize();
    drawFrame(reducedMotion ? 4200 : renderTime * 1000);
  }

  function setReducedMotion(nextValue) {
    reducedMotion = nextValue;

    if (reducedMotion) {
      stop();
      renderStill();
      return;
    }

    start();
  }

  resize();

  return {
    resize,
    start,
    stop,
    setReducedMotion,
    renderStill,
  };
}

const backgroundRenderer = siteMesh ? createBackgroundRenderer(siteMesh) : null;

function syncBackgroundMotion() {
  if (!backgroundRenderer) {
    return;
  }

  if (prefersReducedMotion.matches) {
    backgroundRenderer.setReducedMotion(true);
  } else {
    backgroundRenderer.setReducedMotion(false);
  }
}

if (menuToggle && topbar) {
  menuToggle.addEventListener("click", () => {
    const isOpen = topbar.classList.toggle("menu-open");
    menuToggle.setAttribute("aria-expanded", String(isOpen));
  });
}

if ("scrollRestoration" in window.history) {
  window.history.scrollRestoration = "manual";
}

triggers.forEach((trigger) => {
  trigger.addEventListener("click", (event) => {
    const targetWindow = trigger.dataset.windowTarget;

    if (!panelNames.has(targetWindow)) {
      return;
    }

    event.preventDefault();

    if (window.location.hash !== `#${targetWindow}`) {
      window.history.pushState({}, "", `#${targetWindow}`);
    }

    window.scrollTo({
      top: 0,
      behavior: "auto",
    });

    activateWindow(targetWindow);
  });
});

document.addEventListener("click", (event) => {
  if (!topbar || !topbar.classList.contains("menu-open")) {
    return;
  }

  if (topbar.contains(event.target)) {
    return;
  }

  closeMobileMenu();
});

window.addEventListener("pointermove", (event) => {
  if (prefersMobileBackground.matches) {
    return;
  }
  setPointerTarget(event.clientX, event.clientY);
});

window.addEventListener("pointerleave", resetPointerTarget);
window.addEventListener("blur", resetPointerTarget);

window.addEventListener("keydown", (event) => {
  if (event.key === "Escape") {
    closeMobileMenu();
  }
});

window.addEventListener("resize", () => {
  syncPointerGlow(true);
  syncNavIndicator({ instant: true });
  backgroundRenderer?.resize();
});

window.addEventListener("hashchange", () => {
  activateWindow(getWindowFromHash());
});

window.addEventListener("popstate", () => {
  activateWindow(getWindowFromHash());
});

prefersReducedMotion.addEventListener("change", syncBackgroundMotion);
prefersMobileBackground.addEventListener("change", syncBackgroundMotion);

document.addEventListener("visibilitychange", () => {
  if (!backgroundRenderer) {
    return;
  }

  if (document.hidden) {
    backgroundRenderer.stop();
    return;
  }

  syncBackgroundMotion();
});

activateWindow(getWindowFromHash());
syncPointerGlow(true);
syncBackgroundMotion();
syncNavIndicator({ instant: true });

document.fonts?.ready.then(() => {
  syncNavIndicator({ instant: true });
});
