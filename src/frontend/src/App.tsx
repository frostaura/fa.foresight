import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import Layout from "./components/Layout";
import { ConfirmProvider } from "./components/ConfirmDialog";
import Models from "./pages/Models";
import ModelDesignerPage from "./pages/ModelDesignerPage";
import PaperTrading from "./pages/PaperTrading";
import Status from "./pages/Status";
import Live from "./pages/Live";

export default function App() {
  return (
    <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <ConfirmProvider>
        <Routes>
          <Route element={<Layout />}>
            {/* Default → Trading → Status */}
            <Route index element={<Navigate to="/trading/status" replace />} />

            {/* Trading group */}
            <Route path="trading">
              <Route index element={<Navigate to="/trading/status" replace />} />
              <Route path="status" element={<Status />} />
              <Route path="live" element={<Live />} />
              <Route path="paper" element={<PaperTrading />} />
            </Route>

            {/* Models + Backtesting — kept at /models; the ?view=backtesting param
                drives the sub-tab, matching the existing Models page pattern. */}
            <Route path="models" element={<Models />} />
            {/* Alias so the nav link /models/backtesting opens models with backtesting tab pre-selected */}
            <Route path="models/backtesting" element={<Navigate to="/models?view=backtesting" replace />} />
            {/* Per-model flow designer — renders inside the Layout shell so the sidebar stays visible */}
            <Route path="models/:modelId/designer" element={<ModelDesignerPage />} />

            {/* Legacy redirect — paper-trading used to be the root route */}
            <Route path="paper-trading" element={<Navigate to="/trading/paper" replace />} />

            {/* Catch-all */}
            <Route path="*" element={<Navigate to="/trading/status" replace />} />
          </Route>
        </Routes>
      </ConfirmProvider>
    </BrowserRouter>
  );
}
