import { useState, useEffect } from 'react';
import { photinoBridge } from '../../bridge';
import type { PanelRenderProps } from '../../types';
import { AvatarImage } from '../common/AvatarImage';
import { PanelHeader } from '../common/PanelHeader';

export function FriendsPanel({ launcher, friendDraft, setFriendDraft, setFriendDirty }: PanelRenderProps) {
  const friends = launcher.friends;
  const friendItems = ((friends.friends ?? []) as Array<Record<string, any>>);
  const incomingRequests = ((friends.IncomingRequests ?? []) as Array<Record<string, any>>);

  const [activeTab, setActiveTab] = useState<'all' | 'online' | 'requests'>('all');
  const [searchQuery, setSearchQuery] = useState('');

  // Trigger layout change event on tab switch to recalculate liquid glass/WebGl textures
  useEffect(() => {
    const timer = setTimeout(() => {
      window.dispatchEvent(new Event('vesper-layout-change'));
    }, 150);
    return () => clearTimeout(timer);
  }, [activeTab]);

  // Filtering lists based on search query
  const query = searchQuery.trim().toLowerCase();
  
  const filteredFriends = friendItems.filter(f => {
    const matchesQuery = !query || String(f.username).toLowerCase().includes(query);
    if (activeTab === 'online') {
      return f.isOnline && matchesQuery;
    }
    return matchesQuery;
  });

  const filteredRequests = incomingRequests.filter(r => 
    !query || String(r.username).toLowerCase().includes(query)
  );

  return (
    <>
      <PanelHeader title="Друзья" />

      <section className="mods-shell">
        {/* User Profile Card */}
        <div className="mod-detail-card" style={{ padding: '12px 16px', display: 'flex', gap: '16px', alignItems: 'center', marginBottom: '14px', background: 'rgba(0, 0, 0, 0.45)' }}>
          <div style={{ display: 'flex', gap: '12px', alignItems: 'center', flex: 1 }}>
            <div className="mod-detail-icon-container" style={{ width: '48px', height: '48px', minWidth: '48px', borderRadius: '10px', overflow: 'hidden' }}>
              <AvatarImage
                url={friends.profileAvatarUrl}
                placeholder={friends.profileAvatarPlaceholder}
                alt={String(friends.profileNickname || 'profile')}
              />
            </div>
            <div>
              <span style={{ fontSize: '10px', color: 'rgba(255, 255, 255, 0.45)', textTransform: 'uppercase', letterSpacing: '0.5px' }}>Мой профиль</span>
              <h2 style={{ fontSize: '16px', margin: '1px 0 2px 0', fontWeight: 'bold', color: '#fff' }}>
                {friends.profileNickname || '—'}
              </h2>
              <span className="mod-detail-badge" style={{ fontSize: '9px', padding: '1px 6px', background: 'rgba(255,255,255,0.08)', color: 'rgba(255,255,255,0.7)' }}>
                {friends.profileType || 'Тип входа: оффлайн'}
              </span>
            </div>
          </div>
          
          <div style={{ height: '40px', width: '1px', background: 'rgba(255, 255, 255, 0.1)' }} />

          <div style={{ flex: 1.1 }}>
            <span style={{ fontSize: '10px', color: 'rgba(255, 255, 255, 0.45)', textTransform: 'uppercase', letterSpacing: '0.5px', display: 'block', marginBottom: '4px' }}>
              Добавить друга
            </span>
            <div style={{ display: 'flex', gap: '6px' }}>
              <input
                className="launcher-input"
                value={friendDraft}
                onChange={(event) => {
                  setFriendDirty(true);
                  setFriendDraft(event.target.value);
                  photinoBridge.sendCommand('friends.setNickname', { value: event.target.value });
                }}
                placeholder="Никнейм игрока..."
                style={{ flex: 1, minHeight: '30px', height: '30px', borderRadius: '8px', fontSize: '12px', padding: '0 10px' }}
              />
              <button
                className="primary-button compact left-liquid-glass-button settings-liquid-glass-button"
                disabled={!friends.canManage}
                onClick={() => {
                  setFriendDirty(false);
                  photinoBridge.sendCommand('friends.add');
                }}
                style={{ height: '30px', minWidth: '30px', borderRadius: '8px', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 0 }}
                type="button"
              >
                <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                <span className="left-liquid-glass-content" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round">
                    <line x1="12" y1="5" x2="12" y2="19" />
                    <line x1="5" y1="12" x2="19" y2="12" />
                  </svg>
                </span>
              </button>
            </div>
          </div>
        </div>

        {/* Toolbar & Filter Tabs */}
        <div style={{ marginBottom: '10px' }}>
          <div className={`settings-segmented-toggle friends-toggle left-liquid-glass-button settings-liquid-glass-button ${
            activeTab === 'all' ? 'is-tab0-active' :
            activeTab === 'online' ? 'is-tab1-active' :
            'is-tab2-active'
          }`} role="group" aria-label="Фильтр друзей" style={{ marginBottom: '6px' }}>
            <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
            <button className={activeTab === 'all' ? 'active' : ''} onClick={() => setActiveTab('all')} type="button">
              Все ({friendItems.length})
            </button>
            <button className={activeTab === 'online' ? 'active' : ''} onClick={() => setActiveTab('online')} type="button">
              В сети ({friendItems.filter(f => f.isOnline).length})
            </button>
            <button className={activeTab === 'requests' ? 'active' : ''} onClick={() => setActiveTab('requests')} type="button">
              Запросы ({incomingRequests.length})
            </button>
          </div>

          <label className="field-label search-label" style={{ width: '100%', margin: 0 }}>
            Поиск
            <input
              className="launcher-input"
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              placeholder="Поиск друга по никнейму..."
              style={{ minHeight: '36px', height: '36px' }}
            />
          </label>
        </div>

        {/* Friends Grid */}
        <div className="friends-grid">
          {activeTab === 'requests' ? (
            filteredRequests.length === 0 ? (
              <p className="friends-empty-copy" style={{ gridColumn: '1 / -1', textAlign: 'center', padding: '24px 0', color: 'rgba(255,255,255,0.4)', fontSize: '12px' }}>
                Нет входящих запросов.
              </p>
            ) : (
              filteredRequests.map((req) => (
                <article key={String(req.requestId)} className="friend-row-card">
                  <div style={{ display: 'flex', gap: '10px', alignItems: 'center', flex: 1, minWidth: 0 }}>
                    <div className="mod-detail-icon-container" style={{ width: '32px', height: '32px', minWidth: '32px', borderRadius: '6px', overflow: 'hidden' }}>
                      <AvatarImage url={req.avatarUrl} placeholder={req.avatarPlaceholder} alt={String(req.username)} />
                    </div>
                    <div style={{ display: 'grid', minWidth: 0 }}>
                      <h3 style={{ textOverflow: 'ellipsis', overflow: 'hidden', whiteSpace: 'nowrap', fontSize: '13px', margin: 0, color: '#fff' }}>
                        {req.username}
                      </h3>
                      <span style={{ fontSize: '10px', color: 'rgba(255, 255, 255, 0.45)', whiteSpace: 'nowrap', textOverflow: 'ellipsis', overflow: 'hidden' }}>
                        {req.subtitleText}
                      </span>
                    </div>
                  </div>

                  <div className="friend-card-actions">
                    <button
                      className="primary-button compact"
                      onClick={() => photinoBridge.sendCommand('friends.respond', { requestId: req.requestId, action: 'accept' })}
                      type="button"
                    >
                      Принять
                    </button>
                    <button
                      className="danger-button compact"
                      onClick={() => photinoBridge.sendCommand('friends.respond', { requestId: req.requestId, action: 'decline' })}
                      type="button"
                    >
                      Отклонить
                    </button>
                  </div>
                </article>
              ))
            )
          ) : (
            filteredFriends.length === 0 ? (
              <p className="friends-empty-copy" style={{ gridColumn: '1 / -1', textAlign: 'center', padding: '24px 0', color: 'rgba(255,255,255,0.4)', fontSize: '12px' }}>
                Список друзей пуст.
              </p>
            ) : (
              filteredFriends.map((friend) => (
                <article key={String(friend.username)} className="friend-row-card">
                  <div style={{ display: 'flex', gap: '10px', alignItems: 'center', flex: 1, minWidth: 0 }}>
                    <div className="mod-detail-icon-container" style={{ width: '32px', height: '32px', minWidth: '32px', borderRadius: '6px', overflow: 'hidden', position: 'relative' }}>
                      <AvatarImage url={friend.avatarUrl} placeholder={friend.avatarPlaceholder} alt={String(friend.username)} />
                      <span className={`presence-dot ${friend.isOnline ? 'online' : ''}`} style={{
                        position: 'absolute',
                        bottom: '-1px',
                        right: '-1px',
                        width: '8px',
                        height: '8px',
                        borderRadius: '50%',
                        border: '1.5px solid #000',
                        background: friend.isOnline ? '#00e676' : '#9e9e9e',
                        boxShadow: '0 0 4px rgba(0,0,0,0.5)',
                        zIndex: 2
                      }} />
                    </div>
                    <div style={{ display: 'grid', minWidth: 0 }}>
                      <h3 style={{ textOverflow: 'ellipsis', overflow: 'hidden', whiteSpace: 'nowrap', fontSize: '13px', margin: 0, color: '#fff' }}>
                        {friend.username}
                      </h3>
                      <span style={{ fontSize: '10px', color: 'rgba(255, 255, 255, 0.45)', whiteSpace: 'nowrap', textOverflow: 'ellipsis', overflow: 'hidden' }} title={friend.activityText || friend.presenceText}>
                        {friend.activityText || friend.presenceText}
                      </span>
                    </div>
                  </div>

                  <div className="friend-card-actions">
                    {friend.canConnect ? (
                      <button
                        className="primary-button compact"
                        onClick={() => photinoBridge.sendCommand('friends.connect', { username: friend.username })}
                        type="button"
                      >
                        Войти
                      </button>
                    ) : null}
                    <button
                      className="danger-button compact"
                      disabled={!friends.canManage}
                      onClick={() => photinoBridge.sendCommand('friends.remove', { username: friend.username })}
                      type="button"
                    >
                      Удалить
                    </button>
                  </div>
                </article>
              ))
            )
          )}
        </div>

        {/* System status description */}
        <p style={{ fontSize: '11px', color: 'rgba(255, 255, 255, 0.3)', marginTop: '16px', textAlign: 'center', lineHeight: '1.4' }}>
          {friends.vesperNetStatus}
        </p>
      </section>
    </>
  );
}
