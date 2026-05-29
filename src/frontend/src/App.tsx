import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import Layout from "./components/Layout";
import { ConfirmProvider } from "./components/ConfirmDialog";
import { ModelTrainGateProvider } from "./components/ModelTrainGate";
import Models from "./pages/Models";
import ModelDesignerPage from "./pages/ModelDesignerPage";
import PaperTrading from "./pages/PaperTrading";
import Status from "./pages/Status";
import Live from "./pages/Live";
import Testing from "./pages/Testing";
import Strategies from "./pages/Strategies";
import StrategyDesignerPage from "./pages/StrategyDesignerPage";

export default function App() {
  return (
    <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <ConfirmProvider>
        <ModelTrainGateProvider>
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

            {/* Models — collection only (no sub-tabs). Flow designer stays at /:modelId/designer. */}
            <Route path="models" element={<Models />} />
            {/* Per-model flow designer — renders inside the Layout shell so the sidebar stays visible */}
            <Route path="models/:modelId/designer" element={<ModelDesignerPage />} />
            {/* Legacy alias — redirect old backtesting sub-tab URL to Testing page */}
            <Route path="models/backtesting" element={<Navigate to="/testing?tab=backtest" replace />} />

            {/* Strategies — collection */}
            <Route path="strategies" element={<Strategies />} />
            {/* Per-strategy flow designer — renders inside Layout shell */}
            <Route path="strategies/:strategyId/designer" element={<StrategyDesignerPage />} />

            {/* Testing — backtesting + chaos/bust testing */}
            <Route path="testing" element={<Testing />} />

            {/* Legacy redirect — paper-trading used to be the root route */}
            <Route path="paper-trading" element={<Navigate to="/trading/paper" replace />} />

            {/* Catch-all */}
            <Route path="*" element={<Navigate to="/trading/status" replace />} />
          </Route>
        </Routes>
        </ModelTrainGateProvider>
      </ConfirmProvider>
    </BrowserRouter>
  );
}
