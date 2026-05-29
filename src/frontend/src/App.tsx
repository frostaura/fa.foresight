import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import Layout from "./components/Layout";
import { ConfirmProvider } from "./components/ConfirmDialog";
import MarketsLayout from "./pages/MarketsLayout";
import Models from "./pages/Models";
import PaperTrading from "./pages/PaperTrading";
import Channels from "./pages/Channels";

export default function App() {
  return (
    <BrowserRouter>
      <ConfirmProvider>
        <Routes>
          <Route element={<Layout />}>
            <Route index element={<Navigate to="/paper-trading" replace />} />
            <Route path="markets/*" element={<MarketsLayout />} />
            <Route path="paper-trading" element={<PaperTrading />} />
            <Route path="models" element={<Models />} />
            <Route path="channels" element={<Channels />} />
            <Route path="*" element={<Navigate to="/paper-trading" replace />} />
          </Route>
        </Routes>
      </ConfirmProvider>
    </BrowserRouter>
  );
}
