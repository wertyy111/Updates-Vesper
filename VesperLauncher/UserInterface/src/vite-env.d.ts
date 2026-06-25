/// <reference types="vite/client" />

declare global {
  interface Window {
    liquidGL?: (options?: Record<string, unknown>) => unknown;
  }
}

export {};
