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

export function ModsPanel({ launcher, toggleSelectedMod }: PanelRenderProps) {
  const mods = launcher.mods;
  const selectedCategory = String(mods.selectedCategory ?? 'Моды');
  const activeTab = MODS_CATEGORY_TABS.find((tab) => tab.category === selectedCategory) ?? MODS_CATEGORY_TABS[0];
  const items = ((mods.items ?? []) as Array<Record<string, any>>)
    .filter((item) => String(item.contentKind ?? 'mod').toLowerCase() === activeTab.contentKind);
  const selectedProjectIds = new Set<string>((mods.selectedProjectIds ?? []) as string[]);
  const isCatalogLoading = Boolean(mods.isCatalogLoading ?? mods.isRefreshing);
  const hasSearchQuery = String(mods.searchQuery ?? '').trim().length > 0;
  const emptyMessage = hasSearchQuery
    ? 'По этому запросу ничего не найдено.'
    : activeTab.contentKind === 'mod'
      ? MODS_EMPTY_HINT
      : mods.catalogSummary || mods.summary || `В категории "${activeTab.label}" пока ничего не найдено.`;

  return (
    <>
      <PanelHeader title="Моды" />

      <section className="wpf-section-shell mods-shell">
        <label className="field-label">
          Поиск
          <input className="launcher-input" value={mods.searchQuery ?? ''} onChange={(event) => photinoBridge.sendCommand('mods.setSearch', { value: event.target.value })} placeholder="Название, сборка, описание" />
        </label>

        <div className="mods-category-tabs">
          {MODS_CATEGORY_TABS.map((tab) => (
            <button
              key={tab.category}
              aria-pressed={activeTab.category === tab.category}
              className={`subtle-button mods-category-button ${activeTab.category === tab.category ? 'active' : ''}`}
              onClick={() => photinoBridge.sendCommand('mods.selectCategory', { category: tab.category })}
              type="button"
            >
              {tab.label}
            </button>
          ))}
        </div>

        <div className="catalog-grid">
          {isCatalogLoading ? <p className="friends-empty-copy">Загружаю каталог модов...</p> : null}
          {!isCatalogLoading && items.length === 0 ? <p className="friends-empty-copy">{emptyMessage}</p> : null}
          {items.map((item) => (
            <article key={String(item.projectId)} className={`catalog-card ${selectedProjectIds.has(String(item.projectId)) ? 'selected' : ''}`}>
              <div className="catalog-head">
                <CatalogIcon url={item.iconUrl} fallbackUrl={item.sourceIconUrl} name={item.displayName} />
                <div>
                  <h3>{item.displayName}</h3>
                  <p>{item.description}</p>
                </div>
              </div>

              <div className="catalog-meta">
                {item.badgeText ? <span className="badge" style={{ background: item.badgeBackgroundHex, color: item.badgeForegroundHex }}>{item.badgeText}</span> : null}
                <span className="content-type">{item.contentKind}</span>
              </div>

              <p className="support-copy">{item.packSummary}</p>

              <div className="catalog-actions">
                <button className="chip-button" onClick={() => toggleSelectedMod(String(item.projectId))} type="button">{selectedProjectIds.has(String(item.projectId)) ? 'Убрать из выбора' : 'Выбрать'}</button>
                <button className="chip-button" onClick={() => photinoBridge.sendCommand('mods.toggleFavorite', { projectId: item.projectId })} type="button">{item.isFavorite ? 'В избранном' : 'В избранное'}</button>
                <button className={item.isInstalled ? 'danger-button compact' : 'primary-button compact'} onClick={() => photinoBridge.sendCommand('mods.toggleItem', { projectId: item.projectId })} type="button">{item.actionText}</button>
              </div>
            </article>
          ))}
        </div>
      </section>
    </>
  );
}
