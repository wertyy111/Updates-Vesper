import { useEffect, useState } from 'react';

function buildCatalogInitials(name: unknown) {
  const value = String(name || 'mod').trim().replace(/([a-zа-яё])([A-ZА-ЯЁ])/g, '$1 $2');
  const words = value.split(/[\s._-]+/).filter(Boolean);

  if (words.length === 0) {
    return 'MD';
  }

  if (/^\d+d$/i.test(words[0])) {
    return words[0].toUpperCase();
  }

  if (words.length > 1) {
    return `${words[0][0] ?? ''}${words[1][0] ?? ''}`.toUpperCase();
  }

  return words[0].slice(0, 2).toUpperCase();
}

function encodeLauncherFilePath(path: string) {
  const bytes = new TextEncoder().encode(path);
  let binary = '';
  bytes.forEach((byte) => {
    binary += String.fromCharCode(byte);
  });

  return btoa(binary).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
}

function resolveCatalogIconUrl(url: string) {
  const value = url.trim();
  if (!value) {
    return null;
  }

  if (value.startsWith('/launcher-file/')) {
    return value;
  }

  if (/^[a-z]:[\\/]/i.test(value)) {
    return `/launcher-file/${encodeLauncherFilePath(value)}`;
  }

  try {
    const parsed = new URL(value, window.location.href);
    if (parsed.protocol === 'file:') {
      const filePath = decodeURIComponent(parsed.pathname)
        .replace(/^\/([a-z]:\/)/i, '$1')
        .replace(/\//g, '\\');
      return `/launcher-file/${encodeLauncherFilePath(filePath)}`;
    }
  } catch {
    // Keep the original value below.
  }

  return value;
}

export function CatalogIcon({ url, fallbackUrl, name }: { url?: unknown; fallbackUrl?: unknown; name?: unknown }) {
  void name;
  const [failedUrls, setFailedUrls] = useState<ReadonlySet<string>>(() => new Set());
  const resolvedUrl = typeof url === 'string' && url.trim().length > 0 ? resolveCatalogIconUrl(url) : null;
  const resolvedFallbackUrl = typeof fallbackUrl === 'string' && fallbackUrl.trim().length > 0 ? resolveCatalogIconUrl(fallbackUrl) : null;
  const imageUrl = resolvedUrl && !failedUrls.has(resolvedUrl)
    ? resolvedUrl
    : resolvedFallbackUrl && !failedUrls.has(resolvedFallbackUrl)
      ? resolvedFallbackUrl
      : null;

  useEffect(() => {
    setFailedUrls(new Set());
  }, [resolvedUrl, resolvedFallbackUrl]);

  return (
    <div className={`catalog-icon ${imageUrl ? '' : 'fallback'}`}>
      {imageUrl ? (
        <img
          className="loaded"
          src={imageUrl}
          alt=""
          loading="eager"
          decoding="async"
          referrerPolicy="no-referrer"
          onError={() => {
            setFailedUrls((failed) => new Set(failed).add(imageUrl));
          }}
        />
      ) : null}
    </div>
  );
}
