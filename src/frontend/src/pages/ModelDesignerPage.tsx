import { useNavigate, useParams } from "react-router-dom";
import FlowDesigner from "../components/FlowDesigner";
import { useListModelsQuery } from "../store/api";

/**
 * Full-page route for the model flow designer.
 *
 * Route: /models/:modelId/designer
 *
 * Renders inside the normal Layout shell (sidebar + top bar), so the user
 * always has navigation context while authoring. The designer fills the
 * entire main content area below the Layout outlet.
 *
 * "Back" / onClose navigates to /models so the user returns to the model grid.
 */
export default function ModelDesignerPage() {
  const { modelId } = useParams<{ modelId: string }>();
  const navigate = useNavigate();
  const { data: models, isLoading } = useListModelsQuery();

  const model = models?.find((m) => m.id === modelId);

  if (isLoading) {
    return (
      <div className="h-full flex items-center justify-center text-fa-frost-dim text-sm">
        Loading model…
      </div>
    );
  }

  if (!model) {
    return (
      <div className="h-full flex items-center justify-center text-fa-frost-dim text-sm">
        Model not found.{" "}
        <button
          onClick={() => navigate("/models")}
          className="ml-2 text-fa-frost-bright underline hover:no-underline"
        >
          Back to Models
        </button>
      </div>
    );
  }

  return (
    <div className="h-full flex flex-col overflow-hidden">
      <FlowDesigner model={model} onClose={() => navigate("/models")} />
    </div>
  );
}
