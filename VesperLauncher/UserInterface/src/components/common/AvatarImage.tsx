import { useEffect, useState } from 'react';

export function AvatarImage({ url, placeholder, alt, className }: { url?: unknown; placeholder?: unknown; alt: string; className?: string }) {
  const [failed, setFailed] = useState(false);
  const resolvedUrl = typeof url === 'string' && url.trim().length > 0 ? url : null;
  const resolvedPlaceholder = String(placeholder || alt.slice(0, 2).toUpperCase() || 'AV');

  useEffect(() => {
    setFailed(false);
  }, [resolvedUrl]);

  if (!resolvedUrl || failed) {
    return <span className={className}>{resolvedPlaceholder}</span>;
  }

  return (
    <img
      className={className}
      src={resolvedUrl}
      alt={alt}
      loading="lazy"
      decoding="async"
      onError={() => setFailed(true)}
    />
  );
}
