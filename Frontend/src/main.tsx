import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '@fontsource/roboto/700.css';
import '@fontsource/manrope/800.css';
import './globals.css';
import App from './App';
import { AppProvider } from './Context/AppContext';
import { hydrate } from './lib/persist';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5 * 60_000,
      gcTime: 30 * 60_000,
      refetchOnWindowFocus: false,
      retry: 1,
    },
  },
});

document.addEventListener('contextmenu', (e) => e.preventDefault());

// Wait for Roboto so text never renders in a fallback font first and then jumps.
const fontsReady = Promise.race([
  Promise.all([
    document.fonts.load('400 1em Roboto'),
    document.fonts.load('500 1em Roboto'),
    document.fonts.load('700 1em Roboto'),
    document.fonts.load('800 1em Manrope'),
  ]),
  new Promise((resolve) => setTimeout(resolve, 1500)),
]);

// Restore the last session's data at the same time so the first paint already has channels and matches.
void Promise.allSettled([hydrate(queryClient), fontsReady]).finally(() =>
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <AppProvider>
          <App />
        </AppProvider>
      </QueryClientProvider>
    </StrictMode>,
  ),
);
