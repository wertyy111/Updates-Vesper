import { photinoBridge } from '../../bridge';
import type { PanelRenderProps } from '../../types';
import { AvatarImage } from '../common/AvatarImage';

const friendsText = {
  title: '\u0414\u0440\u0443\u0437\u044c\u044f',
  friends: '\u0414\u0440\u0443\u0437\u0435\u0439',
  incoming: '\u0432\u0445\u043e\u0434\u044f\u0449\u0438\u0445',
  outgoing: '\u0438\u0441\u0445\u043e\u0434\u044f\u0449\u0438\u0445',
  myProfile: '\u041c\u043e\u0439 \u043f\u0440\u043e\u0444\u0438\u043b\u044c',
  nickname: '\u041d\u0438\u043a',
  offlineType: '\u0422\u0438\u043f \u0432\u0445\u043e\u0434\u0430: \u043e\u0444\u0444\u043b\u0430\u0439\u043d',
  addFriend: '\u0414\u043e\u0431\u0430\u0432\u0438\u0442\u044c \u0434\u0440\u0443\u0433\u0430',
  addFriendHint: '\u0412\u0432\u0435\u0434\u0438 \u043d\u0438\u043a \u0438 \u043e\u0442\u043f\u0440\u0430\u0432\u044c \u0437\u0430\u044f\u0432\u043a\u0443.',
  enterNickname: '\u0412\u0432\u0435\u0434\u0438\u0442\u0435 \u043d\u0438\u043a',
  sendRequest: '\u041e\u0442\u043f\u0440\u0430\u0432\u0438\u0442\u044c \u0437\u0430\u044f\u0432\u043a\u0443',
  emptyFriends: '\u0421\u043f\u0438\u0441\u043e\u043a \u0434\u0440\u0443\u0437\u0435\u0439 \u043f\u0443\u0441\u0442.',
  connect: '\u041f\u043e\u0434\u043a\u043b\u044e\u0447\u0438\u0442\u044c\u0441\u044f',
  remove: '\u0423\u0434\u0430\u043b\u0438\u0442\u044c',
  incomingRequests: '\u0412\u0445\u043e\u0434\u044f\u0449\u0438\u0435 \u0437\u0430\u044f\u0432\u043a\u0438',
  emptyRequests: '\u041d\u043e\u0432\u044b\u0445 \u0437\u0430\u044f\u0432\u043e\u043a \u043d\u0435\u0442.',
  accept: '\u041f\u0440\u0438\u043d\u044f\u0442\u044c',
  decline: '\u041e\u0442\u043a\u043b\u043e\u043d\u0438\u0442\u044c',
};

export function FriendsPanel({ launcher, friendDraft, setFriendDraft, setFriendDirty }: PanelRenderProps) {
  const friends = launcher.friends;
  const friendItems = (friends.friends ?? []) as Array<Record<string, any>>;
  const incomingRequests = (friends.incomingRequests ?? []) as Array<Record<string, any>>;
  const outgoingCount = Number(friends.outgoingRequestCount ?? 0);
  const friendSummary = `${friendsText.friends}: ${friendItems.length} \u00b7 ${friendsText.incoming}: ${incomingRequests.length} \u00b7 ${friendsText.outgoing}: ${outgoingCount}`;

  return (
    <div className="friends-wpf-panel">
      <h2 className="friends-title">{friendsText.title}</h2>

      <section className="friends-profile-card">
        <div className="friends-profile-avatar">
          <AvatarImage
            url={friends.profileAvatarUrl}
            placeholder={friends.profileAvatarPlaceholder}
            alt={String(friends.profileNickname || 'profile')}
          />
        </div>

        <div className="friends-profile-info">
          <span>{friendsText.myProfile}</span>
          <strong>{friendsText.nickname}: {friends.profileNickname || '-'}</strong>
          <p>{friends.profileType || friendsText.offlineType}</p>
        </div>

        <div className="friends-inline-add">
          <strong>{friendsText.addFriend}</strong>
          <p>{friendsText.addFriendHint}</p>
          <div className="friends-add-row">
            <input
              className="launcher-input"
              value={friendDraft}
              onChange={(event) => {
                setFriendDirty(true);
                setFriendDraft(event.target.value);
                photinoBridge.sendCommand('friends.setNickname', { value: event.target.value });
              }}
              placeholder={friendsText.enterNickname}
            />
            <button
              className="friends-add-button"
              disabled={!friends.canManage}
              onClick={() => {
                setFriendDirty(false);
                photinoBridge.sendCommand('friends.add');
              }}
              title={friendsText.sendRequest}
              type="button"
            >
              +
            </button>
          </div>
        </div>
      </section>

      <section className="friends-list-section">
        <h3>{friendsText.title}</h3>
        <p className="friends-cloud-status">{friends.cloudStatus || friendSummary}</p>
        <p className="friends-vesper-status">{friends.vesperNetStatus}</p>

        <div className="friends-list-shell">
          <div className="friends-list wpf-friends-list">
            {friendItems.length === 0 ? <p className="friends-empty-copy">{friendsText.emptyFriends}</p> : null}
            {friendItems.map((friend) => (
              <article key={String(friend.username)} className="wpf-friend-card">
                <div className="wpf-friend-avatar">
                  <AvatarImage url={friend.avatarUrl} placeholder={friend.avatarPlaceholder} alt={String(friend.username)} />
                </div>

                <div className="wpf-friend-info">
                  <strong>{friend.username}</strong>
                  <p>
                    <span className={`presence-dot ${friend.isOnline ? 'online' : ''}`} />
                    {friend.presenceText}
                  </p>
                  {friend.activityText ? <span>{friend.activityText}</span> : null}
                  {friend.versionText ? <span className="friend-version">{friend.versionText}</span> : null}
                  {friend.joinAddressText ? <span className="friend-address">{friend.joinAddressText}</span> : null}
                </div>

                <div className="wpf-friend-actions">
                  {friend.canConnect ? (
                    <button
                      className="subtle-button compact"
                      onClick={() => photinoBridge.sendCommand('friends.connect', { username: friend.username })}
                      type="button"
                    >
                      {friendsText.connect}
                    </button>
                  ) : null}
                  <button
                    className="danger-button compact"
                    disabled={!friends.canManage}
                    onClick={() => photinoBridge.sendCommand('friends.remove', { username: friend.username })}
                    type="button"
                  >
                    {friendsText.remove}
                  </button>
                </div>
              </article>
            ))}
          </div>
        </div>
      </section>

      <section className={`incoming-friends-section ${incomingRequests.length === 0 ? 'empty' : ''}`}>
        <h3>{friendsText.incomingRequests}</h3>
        <div className="friends-list-shell incoming">
          <div className="friends-list incoming-list">
            {incomingRequests.length === 0 ? <p className="friends-empty-copy">{friendsText.emptyRequests}</p> : null}
            {incomingRequests.map((request) => (
              <article key={String(request.requestId)} className="wpf-friend-card incoming">
                <div className="wpf-friend-avatar">
                  <AvatarImage url={request.avatarUrl} placeholder={request.avatarPlaceholder} alt={String(request.username)} />
                </div>
                <div className="wpf-friend-info">
                  <strong>{request.username}</strong>
                  <p>{request.subtitleText}</p>
                </div>
                <div className="wpf-friend-actions">
                  <button
                    className="subtle-button compact"
                    onClick={() => photinoBridge.sendCommand('friends.respond', { requestId: request.requestId, action: 'accept' })}
                    type="button"
                  >
                    {friendsText.accept}
                  </button>
                  <button
                    className="danger-button compact"
                    onClick={() => photinoBridge.sendCommand('friends.respond', { requestId: request.requestId, action: 'decline' })}
                    type="button"
                  >
                    {friendsText.decline}
                  </button>
                </div>
              </article>
            ))}
          </div>
        </div>
      </section>
    </div>
  );
}
