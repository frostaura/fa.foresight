import { useEffect, useState } from "react";
import { matchPath, Route, Routes, useLocation, useNavigate, type Location } from "react-router-dom";
import Markets from "./Markets";
import MarketDetail from "./MarketDetail";
import SideDrawer from "../components/SideDrawer";

const DETAIL_PATTERN = "/markets/:providerId/:externalId";
const EXIT_DURATION_MS = 250; // must match var(--fa-duration)

export default function MarketsLayout() {
  const location = useLocation();
  const navigate = useNavigate();
  const isDetail = !!matchPath(DETAIL_PATTERN, location.pathname);

  // Snapshot the detail location while it's active so MarketDetail keeps rendering during the
  // slide-out animation even after the URL has changed back to /markets.
  const [detailSnapshot, setDetailSnapshot] = useState<Location | null>(isDetail ? location : null);

  useEffect(() => {
    if (isDetail) setDetailSnapshot(location);
  }, [isDetail, location]);

  useEffect(() => {
    if (isDetail || !detailSnapshot) return;
    const t = setTimeout(() => setDetailSnapshot(null), EXIT_DURATION_MS);
    return () => clearTimeout(t);
  }, [isDetail, detailSnapshot]);

  return (
    <>
      <Markets />
      <SideDrawer open={isDetail} onClose={() => navigate("/markets")}>
        {detailSnapshot && (
          <Routes location={detailSnapshot}>
            <Route path=":providerId/:externalId" element={<MarketDetail />} />
          </Routes>
        )}
      </SideDrawer>
    </>
  );
}
