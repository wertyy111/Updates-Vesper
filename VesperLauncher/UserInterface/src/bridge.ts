import type { HostEnvelope } from './types';

type Listener = (message: HostEnvelope) => void;

declare global {
  interface External {
    sendMessage?: (message: string) => void;
    receiveMessage?: (message: string) => void;
  }

  interface Window {
    chrome?: {
      webview?: {
        addEventListener?: (name: string, handler: (event: { data: unknown }) => void) => void;
      };
    };
    photinoReceiveMessage?: (message: string | HostEnvelope) => void;
  }
}

class PhotinoBridge {
  private listeners = new Set<Listener>();
  private initialized = false;

  subscribe(listener: Listener) {
    this.ensureInitialized();
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  requestSnapshot() {
    this.sendCommand('host.requestSnapshot');
  }

  sendCommand(command: string, payload: Record<string, unknown> = {}) {
    this.ensureInitialized();
    const message = JSON.stringify({ type: 'command', command, payload });
    void this.sendMessageToHost(message);
  }

  private ensureInitialized() {
    if (this.initialized) {
      return;
    }

    this.initialized = true;
    const existingReceive = window.external?.receiveMessage;
    if (window.external) {
      window.external.receiveMessage = (message: string) => {
        existingReceive?.(message);
        this.dispatchRawMessage(message);
      };
    }

    window.photinoReceiveMessage = (message) => this.dispatchRawMessage(message);
    window.addEventListener('message', (event) => this.dispatchRawMessage(event.data));
    document.addEventListener('message', ((event: Event) => {
      const customEvent = event as CustomEvent<unknown>;
      this.dispatchRawMessage(customEvent.detail);
    }) as EventListener);
    window.chrome?.webview?.addEventListener?.('message', (event) => this.dispatchRawMessage(event.data));
  }

  private async sendMessageToHost(message: string) {
    if (await this.trySendHttpBridgeMessage(message)) {
      return;
    }

    window.external?.sendMessage?.(message);
  }

  private async trySendHttpBridgeMessage(message: string) {
    try {
      const response = await fetch('/bridge-message', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: message
      });

      if (!response.ok) {
        return false;
      }

      const text = await response.text();
      this.dispatchRawMessage(text);
      return true;
    } catch {
      return false;
    }
  }

  private dispatchRawMessage(raw: unknown) {
    const envelope = this.parseEnvelope(raw);
    if (!envelope) {
      return;
    }

    this.listeners.forEach((listener) => listener(envelope));
  }

  private parseEnvelope(raw: unknown): HostEnvelope | null {
    if (!raw) {
      return null;
    }

    if (typeof raw === 'object' && raw !== null && 'type' in (raw as Record<string, unknown>)) {
      return raw as HostEnvelope;
    }

    if (typeof raw !== 'string') {
      return null;
    }

    try {
      return JSON.parse(raw) as HostEnvelope;
    } catch {
      return null;
    }
  }
}

export const photinoBridge = new PhotinoBridge();
