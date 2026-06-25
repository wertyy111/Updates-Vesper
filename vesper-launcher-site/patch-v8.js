
(() => {
  const defaults = { x: 72, y: 32 };
  const isMobileViewport = () => window.matchMedia('(max-width: 900px)').matches;
  function setAura(x, y) {
    const nextX = Math.max(0, Math.min(100, x));
    const nextY = Math.max(0, Math.min(100, y));
    document.documentElement.style.setProperty('--cursor-aura-x', nextX + '%');
    document.documentElement.style.setProperty('--cursor-aura-y', nextY + '%');
    document.body.style.setProperty('--pointer-x', nextX + '%');
    document.body.style.setProperty('--pointer-y', nextY + '%');
  }
  function ensureAuraLayers() {
    const bg = document.querySelector('.site-bg');
    if (!bg) return;
    if (isMobileViewport()) {
      bg.querySelector('.cursor-drift')?.remove();
      bg.querySelector('.cursor-aura')?.remove();
      return;
    }
    if (!bg.querySelector('.cursor-drift')) { const drift = document.createElement('div'); drift.className = 'cursor-drift'; bg.append(drift); }
    if (!bg.querySelector('.cursor-aura')) { const aura = document.createElement('div'); aura.className = 'cursor-aura'; bg.append(aura); }
  }
  function prepareHeroTitle() {
    const heroTitle = document.querySelector('.panel-home .window-copy h1');
    if (!heroTitle) return;
    const summary = document.querySelector('.panel-home .window-copy p');
    if (summary) summary.textContent = 'Быстрый старт, профили и моды в спокойном русском интерфейсе.';
    if (heroTitle.dataset.titlePrepared === 'true') return;
    const lines = [];
    heroTitle.childNodes.forEach((node) => {
      if (node.nodeType === Node.TEXT_NODE) {
        const text = node.textContent.trim();
        if (text) lines.push(text);
        return;
      }
      if (node.nodeType === Node.ELEMENT_NODE) {
        const text = node.textContent.trim();
        if (text) lines.push(text);
      }
    });
    if (!lines.length) return;
    heroTitle.textContent = '';
    heroTitle.classList.add('hero-title');
    heroTitle.removeAttribute('data-rise');
    heroTitle.style.removeProperty('--delay');
    heroTitle.setAttribute('aria-label', lines.join(' '));
    lines.forEach((text, index) => {
      const line = document.createElement('span');
      line.className = 'hero-title-line';
      line.style.setProperty('--line-delay', (index * 0.08) + 's');
      const inner = document.createElement('span');
      inner.className = 'hero-title-text';
      inner.dataset.text = text;
      inner.textContent = text;
      line.append(inner);
      heroTitle.append(line);
    });
    heroTitle.dataset.titlePrepared = 'true';
  }
  function restartHeroTitle() {
    const title = document.querySelector('.panel-home .hero-title');
    if (!title) return;
    title.querySelectorAll('.hero-title-text').forEach((node) => { node.style.animation = 'none'; node.offsetHeight; node.style.animation = ''; });
    title.querySelectorAll('.hero-title-line.has-particle-title').forEach((line) => line.__restartParticleTitle?.());
  }
  function createParticleTitle(line, source) {
    if (line.dataset.particleTitle === 'true') return;
    const canvas = document.createElement('canvas');
    canvas.className = 'hero-particle-title';
    canvas.setAttribute('aria-hidden', 'true');
    line.classList.add('has-particle-title');
    source.classList.add('is-particle-source');
    line.append(canvas);

    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const count = 2000;
    const particles = [];
    const pointer = { x: 0, y: 0, inside: false, strength: 0 };
    let width = 0;
    let height = 0;
    let dpr = 1;
    let padX = 88;
    let padY = 64;
    let frame = 0;
    let introStartedAt = 0;

    const random = (() => {
      let seed = 0x8f13a5d7;
      return () => {
        seed ^= seed << 13;
        seed ^= seed >>> 17;
        seed ^= seed << 5;
        return ((seed >>> 0) % 10000) / 10000;
      };
    })();

    function setIntroStart(particle) {
      const introAngle = random() * Math.PI * 2;
      const introDistance = 10 + random() * 28;
      particle.x = particle.homeX + Math.cos(introAngle) * introDistance;
      particle.y = particle.homeY + Math.sin(introAngle) * introDistance * 0.68;
      particle.introDelay = random() * 320;
    }

    function restartIntro() {
      introStartedAt = performance.now();
      particles.forEach(setIntroStart);
      queueDraw();
    }

    function getFont() {
      const styles = getComputedStyle(source);
      return `${styles.fontStyle} ${styles.fontWeight} ${styles.fontSize} ${styles.fontFamily}`;
    }

    function rebuild() {
      const rect = line.getBoundingClientRect();
      const sourceRect = source.getBoundingClientRect();
      if (!rect.width || !rect.height) return;

      padX = Math.max(48, Math.min(110, rect.width * 0.12));
      padY = Math.max(42, Math.min(78, rect.height * 0.62));
      width = Math.ceil(rect.width + padX * 2);
      height = Math.ceil(rect.height + padY * 2);
      dpr = Math.min(window.devicePixelRatio || 1, 2);

      canvas.style.left = `-${padX}px`;
      canvas.style.top = `${-padY + 46}px`;
      canvas.style.width = `${width}px`;
      canvas.style.height = `${height}px`;
      canvas.width = Math.ceil(width * dpr);
      canvas.height = Math.ceil(height * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

      const mask = document.createElement('canvas');
      const maskCtx = mask.getContext('2d');
      mask.width = width;
      mask.height = height;
      maskCtx.clearRect(0, 0, width, height);
      maskCtx.font = getFont();
      maskCtx.textAlign = 'left';
      maskCtx.textBaseline = 'alphabetic';
      maskCtx.fillStyle = '#ffffff';

      const metrics = maskCtx.measureText(source.dataset.text || source.textContent.trim());
      const ascent = metrics.actualBoundingBoxAscent || rect.height * 0.78;
      const descent = metrics.actualBoundingBoxDescent || rect.height * 0.18;
      const sourceHeight = sourceRect.height || rect.height;
      const textX = padX + Math.max(0, sourceRect.left - rect.left);
      const textY = padY + Math.max(0, sourceRect.top - rect.top);
      const baseline = textY + Math.max(ascent + 2, (sourceHeight + ascent - descent) / 2);
      maskCtx.fillText(source.dataset.text || source.textContent.trim(), textX, baseline);

      const data = maskCtx.getImageData(0, 0, width, height).data;
      const candidates = [];
      const step = 2;

      for (let y = 0; y < height; y += step) {
        for (let x = 0; x < width; x += step) {
          const alpha = data[(y * width + x) * 4 + 3];
          if (alpha > 56) candidates.push({ x, y, alpha });
        }
      }

      particles.length = 0;
      introStartedAt = performance.now();
      for (let i = 0; i < count; i += 1) {
        const stride = Math.max(1, candidates.length / count);
        const point = candidates[Math.floor((i + random() * 0.75) * stride) % candidates.length] || { x: padX, y: padY, alpha: 255 };
        const angle = random() * Math.PI * 2;
        const distance = 44 + random() * 96;
        const homeX = point.x + (random() - 0.5) * step;
        const homeY = point.y + (random() - 0.5) * step;
        const particle = {
          homeX,
          homeY,
          x: homeX,
          y: homeY,
          scatterX: Math.cos(angle) * distance,
          scatterY: Math.sin(angle) * distance * 0.72,
          radius: 0.72 + random() * 0.82,
          alpha: Math.max(0.5, point.alpha / 255),
          introDelay: 0,
        };
        setIntroStart(particle);
        particles.push(particle);
      }
    }

    function queueDraw() {
      if (!frame) frame = requestAnimationFrame(draw);
    }

    function draw() {
      frame = 0;
      const now = performance.now();
      pointer.strength += ((pointer.inside ? 1 : 0) - pointer.strength) * 0.08;
      ctx.clearRect(0, 0, width, height);
      let moving = false;

      for (const particle of particles) {
        const introAge = Math.max(0, now - introStartedAt - particle.introDelay);
        const intro = Math.min(1, introAge / 760);
        const introEase = 1 - Math.pow(1 - intro, 3);
        const dx = particle.homeX - pointer.x;
        const dy = particle.homeY - pointer.y;
        const distance = Math.max(1, Math.hypot(dx, dy));
        const localPush = pointer.inside ? Math.max(0, 1 - distance / 150) ** 2.4 : 0;
        const influence = localPush * pointer.strength;
        const pushDistance = 76 * influence;
        const targetX = particle.homeX + (dx / distance) * pushDistance + particle.scatterX * influence * 0.38;
        const targetY = particle.homeY + (dy / distance) * pushDistance + particle.scatterY * influence * 0.38;

        if (Math.abs(targetX - particle.x) > 0.08 || Math.abs(targetY - particle.y) > 0.08) moving = true;
        if (intro < 1) moving = true;
        particle.x += (targetX - particle.x) * 0.16;
        particle.y += (targetY - particle.y) * 0.16;

        ctx.beginPath();
        ctx.fillStyle = `rgba(248, 249, 255, ${particle.alpha * introEase})`;
        ctx.arc(particle.x, particle.y, particle.radius * (0.58 + introEase * 0.42), 0, Math.PI * 2);
        ctx.fill();
      }

      if (pointer.inside || pointer.strength > 0.002 || moving) queueDraw();
    }

    function setPointer(event) {
      const rect = canvas.getBoundingClientRect();
      pointer.x = event.clientX - rect.left;
      pointer.y = event.clientY - rect.top;
    }

    canvas.addEventListener('pointerenter', (event) => {
      pointer.inside = true;
      setPointer(event);
      queueDraw();
    });
    canvas.addEventListener('pointermove', (event) => {
      setPointer(event);
      queueDraw();
    });
    canvas.addEventListener('pointerleave', () => {
      pointer.inside = false;
      queueDraw();
    });

    const resizeObserver = new ResizeObserver(() => {
      rebuild();
      queueDraw();
    });
    resizeObserver.observe(line);
    document.fonts?.ready.then(() => {
      rebuild();
      queueDraw();
    });
    rebuild();
    queueDraw();
    line.__refreshParticleTitle = () => {
      rebuild();
      queueDraw();
    };
    line.__restartParticleTitle = restartIntro;
    line.dataset.particleTitle = 'true';
  }
  function setupHeroParticleTitle() {
    const line = document.querySelector('.panel-home .hero-title-line:first-child');
    const source = line?.querySelector('.hero-title-text');
    if (!line || !source) return;
    if (line.dataset.particleTitle === 'true') {
      line.__refreshParticleTitle?.();
      return;
    }
    createParticleTitle(line, source);
  }
  function refreshHome() {
    ensureAuraLayers();
    prepareHeroTitle();
    if ((window.location.hash || '#home') === '#home') {
      setupHeroParticleTitle();
      restartHeroTitle();
    }
  }
  window.addEventListener('pointermove', (event) => { setAura((event.clientX / Math.max(window.innerWidth, 1)) * 100, (event.clientY / Math.max(window.innerHeight, 1)) * 100); }, { passive: true });
  window.addEventListener('pointerleave', () => setAura(defaults.x, defaults.y));
  window.addEventListener('blur', () => setAura(defaults.x, defaults.y));
  window.addEventListener('hashchange', () => setTimeout(refreshHome, 40));
  window.addEventListener('load', refreshHome);
  refreshHome();
  setAura(defaults.x, defaults.y);
})();
