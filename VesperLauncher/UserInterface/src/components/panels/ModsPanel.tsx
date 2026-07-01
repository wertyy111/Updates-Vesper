import { useState, useEffect } from 'react';
import { photinoBridge } from '../../bridge';
import type { PanelRenderProps } from '../../types';
import { PanelHeader } from '../common/PanelHeader';
import { CatalogIcon } from '../common/CatalogIcon';

const MODS_CATEGORY_TABS = [
  { label: 'Моды', category: 'Моды', contentKind: 'mod' },
  { label: 'Ресурс паки', category: 'Ресурспаки', contentKind: 'resourcepack' },
  { label: 'Сборки', category: 'Сборки', contentKind: 'modpack' },
  { label: 'Шейдеры', category: 'Шейдеры', contentKind: 'shader' }
];

const MODS_EMPTY_HINT = 'Для модов установи или выбери Fabric/Forge-версию в списке версий. На базовой версии Minecraft моды не ставятся.';

const MOCK_CF_DETAILS: Record<string, {
  title: string;
  description: string;
  body?: string;
  downloads: string;
  followers: string;
  iconUrl?: string;
  categories: string[];
}> = {
  'cf-jei': {
    title: 'Just Enough Items (JEI)',
    description: 'Позволяет просматривать рецепты крафта и все доступные предметы прямо в инвентаре.',
    body: `# Just Enough Items (JEI)
JEI создан с нуля для обеспечения высокой производительности и стабильности.

### Особенности
* **Рецепты крафта:** Просмотр крафтов прямо в игре с помощью клавиши 'R'.
* **Использование:** Посмотреть в каких рецептах используется предмет с помощью клавиши 'U'.
* **Удобный поиск:** Быстрый текстовый поиск по всему списку предметов в правой части экрана.`,
    downloads: '248,5M',
    followers: '154K',
    iconUrl: 'https://cdn.modrinth.com/data/u6dRKJwZ/4a3f18ac0d096c9f8e9176984c44be4e58f94c89_96.webp',
    categories: ['Инвентарь', 'Удобство']
  },
  'cf-journeymap': {
    title: 'JourneyMap',
    description: 'Отображает карту мира в реальном времени, миникарту на экране и позволяет ставить метки.',
    body: `# JourneyMap
JourneyMap составляет карту вашего мира в реальном времени по мере вашего исследования.

### Функционал
* **Миникарта:** Настраиваемая миникарта в углу экрана.
* **Полноэкранная карта:** Открывается по нажатию клавиши 'J'.
* **Точки интереса (Waypoints):** Создавайте и просматривайте метки для быстрого ориентирования.`,
    downloads: '142,3M',
    followers: '92K',
    iconUrl: 'https://cdn.modrinth.com/data/lfHFW1mp/a1c571a21a88f6fa59eab67829f216f65ab393ee_96.webp',
    categories: ['Навигация', 'Мини-карта']
  },
  'cf-bop': {
    title: "Biomes O' Plenty",
    description: 'Добавляет огромное количество уникальных биомов с новыми деревьями, цветами и блоками.',
    downloads: '118,7M',
    followers: '78K',
    iconUrl: 'https://cdn.modrinth.com/data/HXF82T3G/ffb870e12c325b795d54833f8f899126553ef06f.png',
    categories: ['Мир', 'Генерация']
  },
  'cf-ironchests': {
    title: 'Iron Chests',
    description: 'Добавляет улучшенные сундуки (железный, золотой, алмазный) с увеличенной вместимостью.',
    downloads: '94,2M',
    followers: '54K',
    iconUrl: 'https://cdn.modrinth.com/data/n2de3t2z/6a17c192e399211a9a0b5c31ec75f5fc073ca7b6.png',
    categories: ['Хранение', 'Сундуки']
  },
  'cf-tweaks': {
    title: 'Mouse Tweaks',
    description: 'Олегает сортировку инвентаря и перемещение предметов с помощью зажатой кнопки мыши.',
    downloads: '128,4M',
    followers: '68K',
    iconUrl: 'https://cdn.modrinth.com/data/aC3cM3Vq/6c0eaa4e60a9c87f4766f222ff63286f09da32c0_96.webp',
    categories: ['Удобство', 'Инвентарь']
  },
  'cf-clumps': {
    title: 'Clumps',
    description: 'Объединяет висящие сферы опыта в одну крупную, существенно уменьшая лаги в игре.',
    downloads: '105,1M',
    followers: '61K',
    iconUrl: 'https://cdn.modrinth.com/data/Wnxd13zP/6a965bb7974c3e759a53a1c89c35de4acd4cf86a_96.webp',
    categories: ['Оптимизация', 'Лаги']
  },
  'cf-faithful': {
    title: 'Faithful 32x',
    description: 'Оригинальные текстуры игры в более высоком и детализированном качестве.',
    downloads: '85,4M',
    followers: '42K',
    iconUrl: 'https://cdn.modrinth.com/data/w0TnApzs/e8403d1fb2f55321ae74402c1e8c90a3a5670856.png',
    categories: ['Ресурспаки', '32x']
  },
  'cf-sphax': {
    title: 'PureBDcraft',
    description: 'Комиксовый и яркий стиль текстур, полностью преображающий мир Minecraft.',
    downloads: '46,1M',
    followers: '31K',
    iconUrl: 'https://bdcraft.net/favicon.ico',
    categories: ['Ресурспаки', 'Мультяшные']
  },
  'cf-bsl': {
    title: 'BSL Shaders',
    description: 'Популярные шейдеры с мягким реалистичным освещением, туманом и красивой водой.',
    downloads: '38,2M',
    followers: '29K',
    iconUrl: 'https://cdn.modrinth.com/data/Q1vvjJYV/2a611a3cb434fb52fb81fa5dace13c5d8b67e55d_96.webp',
    categories: ['Шейдеры', 'Реализм']
  },
  'cf-complimentary': {
    title: 'Complementary Shaders',
    description: 'Идеальный баланс производительности и красоты, сохраняющий дух ванили.',
    downloads: '52,7M',
    followers: '38K',
    iconUrl: 'https://cdn.modrinth.com/data/HVnmMxH1/79cb7c8123bbc54945305b2ebad6b8881efdf5f8_96.webp',
    categories: ['Шейдеры', 'Оптимизация']
  },
  'cf-rlcraft': {
    title: 'RLCraft',
    description: 'Сверхсложная сборка на выживание с драконами, фэнтези и реалистичной физикой.',
    downloads: '18,5M',
    followers: '19K',
    iconUrl: 'https://cdn.modrinth.com/data/Qx4KOI2G/6bce4b7f4a25a49e23d57fcc6838a1c46b0aff72_96.webp',
    categories: ['Сборки', 'Выживание']
  },
  'cf-skyfactory': {
    title: 'SkyFactory 4',
    description: 'Популярный индустриальный скайблок с огромным деревом достижений.',
    downloads: '14,2M',
    followers: '15K',
    iconUrl: 'https://static.wikia.nocookie.net/minecraft_gamepedia/images/1/15/Grass_Block_JE4.png',
    categories: ['Сборки', 'Индустриальные']
  }
};

function formatCount(count: number | string | undefined): string {
  if (count === undefined || count === null) return '0';
  const num = Number(count);
  if (isNaN(num)) return String(count);
  if (num >= 1_000_000) {
    return (num / 1_000_000).toFixed(1).replace(/\.0$/, '') + 'M';
  }
  if (num >= 1_000) {
    return (num / 1_000).toFixed(1).replace(/\.0$/, '') + 'K';
  }
  return String(num);
}

function renderMarkdown(md: string): string {
  if (!md) return '';
  let html = md
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
  
  html = html.replace(/^### (.*$)/gim, '<h3>$1</h3>');
  html = html.replace(/^## (.*$)/gim, '<h2>$1</h2>');
  html = html.replace(/^# (.*$)/gim, '<h1>$1</h1>');
  
  html = html.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>');
  html = html.replace(/\*(.*?)\*/g, '<em>$1</em>');
  
  html = html.replace(/^\* (.*$)/gim, '<li>$1</li>');
  html = html.replace(/<\/li>\n<li>/g, '</li><li>');
  html = html.replace(/(<li>.*<\/li>)/g, '<ul>$1</ul>');
  
  html = html.replace(/\[(.*?)\]\((.*?)\)/g, '<a href="$2" target="_blank">$1</a>');

  html = html.split('\n').map(line => {
    const trimmed = line.trim();
    if (trimmed.startsWith('<h') || trimmed.startsWith('<ul') || trimmed.startsWith('<li') || trimmed.startsWith('</ul')) {
      return line;
    }
    if (!trimmed) return '';
    return `<p>${line}</p>`;
  }).join('\n');

  return html;
}

interface ProjectVersion {
  id: string;
  name: string;
  versionNumber: string;
  gameVersions: string[];
  loaders: string[];
  releaseDate: string;
  filename: string;
  downloadUrl: string;
}

export function ModsPanel({ launcher }: PanelRenderProps) {
  const mods = launcher.mods;
  const selectedCategory = String(mods.selectedCategory ?? 'Моды');
  const activeTab = MODS_CATEGORY_TABS.find((tab) => tab.category === selectedCategory) ?? MODS_CATEGORY_TABS[0];
  const items = ((mods.items ?? []) as Array<Record<string, any>>)
    .filter((item) => String(item.contentKind ?? 'mod').toLowerCase() === activeTab.contentKind);
  const isCatalogLoading = Boolean(mods.isCatalogLoading ?? mods.isRefreshing);
  const hasSearchQuery = String(mods.searchQuery ?? '').trim().length > 0;
  const emptyMessage = hasSearchQuery
    ? 'По этому запросу ничего не найдено.'
    : activeTab.contentKind === 'mod'
      ? MODS_EMPTY_HINT
      : mods.catalogSummary || mods.summary || `В категории "${activeTab.label}" пока ничего не найдено.`;

  // Details View States
  const [selectedProjectDetail, setSelectedProjectDetail] = useState<{
    projectId: string;
    title: string;
    description: string;
    body: string;
    downloads: string;
    followers: string;
    iconUrl?: string;
    categories: string[];
  } | null>(null);
  const [isDetailsLoading, setIsDetailsLoading] = useState(false);
  const [detailsTab, setDetailsTab] = useState<'info' | 'versions'>('info');

  const [projectVersions, setProjectVersions] = useState<ProjectVersion[]>([]);
  const [isVersionsLoading, setIsVersionsLoading] = useState(false);

  // Reset detail page if tab or provider changes
  useEffect(() => {
    setSelectedProjectDetail(null);
  }, [selectedCategory, mods.provider]);

  // Trigger layout change event to re-initialize liquid glass buttons
  useEffect(() => {
    const timer = setTimeout(() => {
      window.dispatchEvent(new Event('vesper-layout-change'));
    }, 150);
    return () => clearTimeout(timer);
  }, [selectedProjectDetail]);

  // Load versions
  useEffect(() => {
    if (detailsTab !== 'versions' || !selectedProjectDetail) {
      setProjectVersions([]);
      return;
    }

    const projectId = selectedProjectDetail.projectId;
    if (mods.provider === 'curseforge') {
      const mockVersions: ProjectVersion[] = [
        {
          id: `${projectId}-1.20.1`,
          name: `${selectedProjectDetail.title} 1.20.1`,
          versionNumber: '1.20.1-1.0',
          gameVersions: ['1.20.1'],
          loaders: ['forge', 'fabric'],
          releaseDate: '2025-10-12T15:30:00Z',
          filename: `${projectId}-1.20.1.jar`,
          downloadUrl: `https://mediafilez.forgecdn.net/files/mock/${projectId}-1.20.1.jar`
        },
        {
          id: `${projectId}-1.19.4`,
          name: `${selectedProjectDetail.title} 1.19.4`,
          versionNumber: '1.19.4-0.9',
          gameVersions: ['1.19.4'],
          loaders: ['forge', 'fabric'],
          releaseDate: '2024-05-18T10:15:00Z',
          filename: `${projectId}-1.19.4.jar`,
          downloadUrl: `https://mediafilez.forgecdn.net/files/mock/${projectId}-1.19.4.jar`
        },
        {
          id: `${projectId}-1.18.2`,
          name: `${selectedProjectDetail.title} 1.18.2`,
          versionNumber: '1.18.2-0.8',
          gameVersions: ['1.18.2'],
          loaders: ['forge', 'fabric'],
          releaseDate: '2023-11-22T08:45:00Z',
          filename: `${projectId}-1.18.2.jar`,
          downloadUrl: `https://mediafilez.forgecdn.net/files/mock/${projectId}-1.18.2.jar`
        }
      ];
      setProjectVersions(mockVersions);
    } else {
      setIsVersionsLoading(true);
      fetch(`https://api.modrinth.com/v2/project/${projectId}/version`)
        .then(res => res.ok ? res.json() : [])
        .then(data => {
          const formatted = data.map((v: any) => {
            const file = v.files?.find((f: any) => f.primary) || v.files?.[0] || {};
            return {
              id: v.id,
              name: v.name,
              versionNumber: v.version_number,
              gameVersions: v.game_versions || [],
              loaders: v.loaders || [],
              releaseDate: v.date_published,
              filename: file.filename || `${projectId}-${v.version_number}.jar`,
              downloadUrl: file.url || ''
            };
          }).filter((v: any) => v.downloadUrl);
          setProjectVersions(formatted);
        })
        .catch(err => {
          console.error("Failed to load versions:", err);
          setProjectVersions([]);
        })
        .finally(() => {
          setIsVersionsLoading(false);
        });
    }
  }, [detailsTab, selectedProjectDetail, mods.provider]);

  const handleCardClick = async (projectId: string) => {
    setIsDetailsLoading(true);
    setDetailsTab('info');
    const catalogItem = items.find(i => String(i.projectId) === projectId);
    const resolvedIconUrl = catalogItem?.iconUrl || catalogItem?.sourceIconUrl;

    if (mods.provider === 'curseforge') {
      const mockDetail = MOCK_CF_DETAILS[projectId];
      if (mockDetail) {
        setSelectedProjectDetail({
          projectId,
          title: mockDetail.title,
          description: mockDetail.description,
          body: mockDetail.body || `# ${mockDetail.title}\n\nПопулярная модификация CurseForge для Minecraft. Доступна для быстрой установки в один клик.`,
          downloads: mockDetail.downloads,
          followers: mockDetail.followers,
          iconUrl: resolvedIconUrl || mockDetail.iconUrl,
          categories: mockDetail.categories
        });
      } else {
        setSelectedProjectDetail({
          projectId,
          title: projectId,
          description: 'CurseForge популярная модификация.',
          body: '# Описание\nМод успешно загружен.',
          downloads: '1.2M',
          followers: '12K',
          iconUrl: resolvedIconUrl,
          categories: ['CurseForge']
        });
      }
      setIsDetailsLoading(false);
    } else {
      try {
        const res = await fetch(`https://api.modrinth.com/v2/project/${projectId}`);
        if (res.ok) {
          const data = await res.json();
          setSelectedProjectDetail({
            projectId: data.id,
            title: data.title,
            description: data.description,
            body: data.body,
            downloads: formatCount(data.downloads),
            followers: formatCount(data.followers),
            iconUrl: resolvedIconUrl || data.icon_url,
            categories: data.categories || []
          });
        }
      } catch (err) {
        console.error("Failed to load project details:", err);
      } finally {
        setIsDetailsLoading(false);
      }
    }
  };

  return (
    <>
      <PanelHeader title="Моды" />

      <section className="mods-shell">
        {selectedProjectDetail ? (
          <div className="mod-detail-view">
            <div className="mod-detail-header-row">
              <button className="mod-detail-back-button" onClick={() => setSelectedProjectDetail(null)} type="button">
                <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2.5">
                  <path d="M19 12H5M12 19l-7-7 7-7" />
                </svg>
                Назад к списку
              </button>

              {(() => {
                const catalogItem = items.find(i => String(i.projectId) === selectedProjectDetail.projectId);
                if (!catalogItem) return null;
                return (
                  <div className="catalog-actions" style={{ margin: 0 }}>
                    <button
                      className={`heart-button ${catalogItem.isFavorite ? 'active' : ''}`}
                      onClick={() => photinoBridge.sendCommand('mods.toggleFavorite', { projectId: catalogItem.projectId })}
                      type="button"
                    >
                      <svg viewBox="0 0 24 24" width="18" height="18" fill={catalogItem.isFavorite ? '#ff4b4b' : 'none'} stroke={catalogItem.isFavorite ? '#ff4b4b' : 'currentColor'} strokeWidth="2.5">
                        <path d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z" />
                      </svg>
                    </button>
                    <button className={catalogItem.isInstalled ? 'danger-button compact' : 'primary-button compact'} onClick={() => photinoBridge.sendCommand('mods.toggleItem', { projectId: catalogItem.projectId })} type="button">{catalogItem.actionText}</button>
                  </div>
                );
              })()}
            </div>

            <div className="mod-detail-card">
              <div className="mod-detail-head">
                <div className="mod-detail-icon-container">
                  <CatalogIcon url={selectedProjectDetail.iconUrl} fallbackUrl="images/pack_icon.png" name={selectedProjectDetail.title} />
                </div>
                <div className="mod-detail-title-block">
                  <h2>{selectedProjectDetail.title}</h2>
                  <p>{selectedProjectDetail.description}</p>
                  
                  <div className="mod-detail-stats">
                    <span className="mod-detail-stat">
                      <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2.5">
                        <path d="M12 5v14M19 12l-7 7-7-7" />
                      </svg>
                      {selectedProjectDetail.downloads} скачиваний
                    </span>
                    <span className="mod-detail-stat">
                      <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2.5">
                        <path d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z" />
                      </svg>
                      {selectedProjectDetail.followers} лайков
                    </span>
                    {selectedProjectDetail.categories.map((cat, idx) => (
                      <span key={idx} className="mod-detail-badge">{cat}</span>
                    ))}
                  </div>
                </div>
              </div>

              <div className="mod-detail-tabs">
                <button className={`mod-detail-tab-button ${detailsTab === 'info' ? 'active' : ''}`} onClick={() => setDetailsTab('info')} type="button">Описание</button>
                <button className={`mod-detail-tab-button ${detailsTab === 'versions' ? 'active' : ''}`} onClick={() => setDetailsTab('versions')} type="button">Версии</button>
              </div>

              {detailsTab === 'info' ? (
                <div className="mod-detail-body" dangerouslySetInnerHTML={{ __html: renderMarkdown(selectedProjectDetail.body) }} />
              ) : (
                <div className="mod-detail-body" style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
                  <h3>Доступные версии</h3>
                  <div className="mod-versions-list">
                    {isVersionsLoading ? (
                      <p className="friends-empty-copy">Загрузка списка версий...</p>
                    ) : projectVersions.length === 0 ? (
                      <p className="friends-empty-copy">Версии не найдены.</p>
                    ) : (
                      projectVersions.map((version) => {
                        const isFileInstalled = ((mods.installedFileNames || []) as string[]).includes(version.filename);
                        return (
                          <div key={version.id} className="mod-version-row">
                            <div className="mod-version-info">
                              <div className="mod-version-title-row">
                                <span className="mod-version-name">{version.name}</span>
                                <span className="mod-version-number">{version.versionNumber}</span>
                              </div>
                              <div className="mod-version-meta-row">
                                <span className="mod-version-date">
                                  {new Date(version.releaseDate).toLocaleDateString('ru-RU')}
                                </span>
                                <div className="mod-version-badges">
                                  {version.loaders.map((l) => (
                                    <span key={l} className="mod-version-badge loader">{l}</span>
                                  ))}
                                  {version.gameVersions.slice(0, 3).map((gv) => (
                                    <span key={gv} className="mod-version-badge game">{gv}</span>
                                  ))}
                                </div>
                              </div>
                            </div>
                            
                            <button
                              className={isFileInstalled ? 'danger-button compact' : 'primary-button compact'}
                              onClick={() => {
                                if (isFileInstalled) {
                                  photinoBridge.sendCommand('mods.toggleItem', { projectId: selectedProjectDetail.projectId });
                                } else {
                                  photinoBridge.sendCommand('mods.installversion', {
                                    projectId: selectedProjectDetail.projectId,
                                    url: version.downloadUrl,
                                    filename: version.filename,
                                    contentKind: activeTab.contentKind
                                  });
                                }
                              }}
                              type="button"
                            >
                              {isFileInstalled ? 'Удалить' : 'Скачать'}
                            </button>
                          </div>
                        );
                      })
                    )}
                  </div>
                </div>
              )}
            </div>
          </div>
        ) : (
          <>
            <div className="mods-toolbar-row">
              <label className="field-label search-label">
                Поиск
                <input className="launcher-input" value={mods.searchQuery ?? ''} onChange={(event) => photinoBridge.sendCommand('mods.setSearch', { value: event.target.value })} placeholder="Название, сборка, описание" />
              </label>

              <div className="provider-selector-wrapper">
                <span className="field-label-text">Источник</span>
                <div className={`settings-segmented-toggle provider-toggle left-liquid-glass-button settings-liquid-glass-button ${mods.provider === 'curseforge' ? 'is-right-active' : ''}`} role="group" aria-label="Источник модов">
                  <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                  <button className={mods.provider === 'modrinth' ? 'active' : ''} onClick={() => photinoBridge.sendCommand('mods.setProvider', { value: 'modrinth' })} type="button">Modrinth</button>
                  <button className={mods.provider === 'curseforge' ? 'active' : ''} onClick={() => photinoBridge.sendCommand('mods.setProvider', { value: 'curseforge' })} type="button">CurseForge</button>
                </div>
              </div>
            </div>

            <div className={`settings-segmented-toggle mods-toggle left-liquid-glass-button settings-liquid-glass-button ${
              activeTab.category === 'Моды' ? 'is-tab0-active' :
              activeTab.category === 'Ресурспаки' ? 'is-tab1-active' :
              activeTab.category === 'Сборки' ? 'is-tab2-active' :
              'is-tab3-active'
            }`} role="group" aria-label="Категория">
              <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
              {MODS_CATEGORY_TABS.map((tab) => (
                <button
                  key={tab.category}
                  className={activeTab.category === tab.category ? 'active' : ''}
                  onClick={() => photinoBridge.sendCommand('mods.selectCategory', { category: tab.category })}
                  type="button"
                >
                  {tab.label}
                </button>
              ))}
            </div>

            <div className="catalog-grid">
              {isCatalogLoading || isDetailsLoading ? <p className="friends-empty-copy">Загружаю каталог...</p> : null}
              {!isCatalogLoading && !isDetailsLoading && items.length === 0 ? <p className="friends-empty-copy">{emptyMessage}</p> : null}
              {!isCatalogLoading && !isDetailsLoading && items.map((item) => (
                <article key={String(item.projectId)} className="catalog-card" onClick={() => handleCardClick(String(item.projectId))} style={{ cursor: 'pointer' }}>
                  <div className="catalog-head">
                    <CatalogIcon url={item.iconUrl} fallbackUrl={item.sourceIconUrl} name={item.displayName} />
                    <div style={{ display: 'grid' }}>
                      <h3 style={{ textOverflow: 'ellipsis', overflow: 'hidden', whiteSpace: 'nowrap' }}>{item.displayName}</h3>
                      <div className="card-stats-row">
                        <span className="stat-item" title="Скачивания">
                          <svg viewBox="0 0 24 24" width="10" height="10" fill="none" stroke="currentColor" strokeWidth="2.5">
                            <path d="M12 5v14M19 12l-7 7-7-7" />
                          </svg>
                          {formatCount(item.downloads)}
                        </span>
                        <span className="stat-item" title="Лайки">
                          <svg viewBox="0 0 24 24" width="10" height="10" fill="none" stroke="currentColor" strokeWidth="2.5">
                            <path d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z" />
                          </svg>
                          {formatCount(item.followers)}
                        </span>
                        {item.badgeText ? (
                          <span className="mod-detail-badge" style={{ fontSize: '9px', padding: '1px 6px', background: item.badgeBackgroundHex, color: item.badgeForegroundHex }}>
                            {item.badgeText}
                          </span>
                        ) : null}
                      </div>
                    </div>
                  </div>

                  <p>{item.description}</p>

                  <div className="catalog-actions" onClick={(e) => e.stopPropagation()}>
                    <button
                      className={`heart-button ${item.isFavorite ? 'active' : ''}`}
                      onClick={() => photinoBridge.sendCommand('mods.toggleFavorite', { projectId: item.projectId })}
                      type="button"
                    >
                      <svg viewBox="0 0 24 24" width="16" height="16" fill={item.isFavorite ? '#ff4b4b' : 'none'} stroke={item.isFavorite ? '#ff4b4b' : 'currentColor'} strokeWidth="2.5">
                        <path d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z" />
                      </svg>
                    </button>
                    <button className={item.isInstalled ? 'danger-button compact' : 'primary-button compact'} onClick={() => photinoBridge.sendCommand('mods.toggleItem', { projectId: item.projectId })} type="button">{item.actionText}</button>
                  </div>
                </article>
              ))}
            </div>
          </>
        )}
      </section>
    </>
  );
}
