import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import ReactFlow, {
  Background,
  Controls,
  type Edge as RFEdge,
  Handle,
  MarkerType,
  type Node as RFNode,
  Position,
  ReactFlowProvider,
  addEdge,
  useEdgesState,
  useNodesState,
  useReactFlow
} from "reactflow";
import "reactflow/dist/style.css";
import { AlertTriangle, Code2, Layout, Loader2, Play, Save, Terminal, Trash2, X } from "lucide-react";
import { cn } from "../lib/cn";
import { ProgressInline } from "./ProgressInline";
import {
  useRunFlowNodeMutation,
  type FlowDefinition,
  type FlowEdge,
  type FlowNode,
  type NodeCatalogueEntry
} from "../store/api";

/**
 * Generic FlowDesigner — serves both model and strategy DAG authoring.
 *
 * Props:
 *   title        — display name in the header strip
 *   definitionJson — the raw JSON string of the FlowDefinition (source of truth)
 *   isBuiltIn    — when true the canvas and code view are both read-only
 *   catalogue    — the NodeCatalogueEntry registry (from useGetNodeCatalogueQuery)
 *   onSave       — called with the serialized FlowDefinition JSON; async or sync
 *   onClose      — called when the user clicks "Back"
 *   entityKind   — "model" | "strategy"; gates model-only features (run-node sandbox)
 *
 * Dual-view (Design ↔ Code):
 *   Design = ReactFlow canvas (unchanged behavior).
 *   Code   = editable <textarea> showing pretty-printed FlowDefinition JSON.
 *   Switching Design→Code serializes via rfToDefinition; Code→Design parses and rebuilds.
 *   Invalid JSON shows an inline error and keeps the user in Code view.
 *   Save from either view: Code view validates first; blocks save on invalid JSON.
 *
 * Auto-layout:
 *   Applied when saved positions are missing or any two nodes overlap.
 *   Left-to-right layered layout (depth = longest-path from source).
 *   fitView ensures the graph is framed nicely on load.
 */

// ── Public props shape ─────────────────────────────────────────────────────────────────────────

export interface FlowDesignerProps {
  title: string;
  definitionJson: string;
  isBuiltIn: boolean;
  catalogue: Record<string, NodeCatalogueEntry>;
  onSave: (definitionJson: string) => Promise<void> | void;
  onClose: () => void;
  entityKind: "model" | "strategy";
}

export default function FlowDesigner(props: FlowDesignerProps) {
  return (
    <ReactFlowProvider>
      <DesignerInner {...props} />
    </ReactFlowProvider>
  );
}

// ── Inner component (needs ReactFlowProvider context) ──────────────────────────────────────────

type ViewMode = "design" | "code";

function DesignerInner({
  title,
  definitionJson,
  isBuiltIn,
  catalogue,
  onSave,
  onClose,
  entityKind,
}: FlowDesignerProps) {
  const [runNode, { isLoading: isRunning }] = useRunFlowNodeMutation();
  const [error, setError] = useState<string | null>(null);
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);
  const [runOutput, setRunOutput] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState(false);

  // Dual-view state
  const [viewMode, setViewMode] = useState<ViewMode>("design");
  const [codeText, setCodeText] = useState<string>("");
  const [codeError, setCodeError] = useState<string | null>(null);

  // Parse the current flow definition once on mount + when it changes.
  const initialDef = useMemo<FlowDefinition | null>(() => {
    try { return JSON.parse(definitionJson); } catch { return null; }
  }, [definitionJson]);

  const [nodes, setNodes, onNodesChange] = useNodesState(definitionToRfNodes(initialDef, catalogue));
  const [edges, setEdges, onEdgesChange] = useEdgesState(definitionToRfEdges(initialDef));
  const { project } = useReactFlow();
  const wrapper = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (initialDef && catalogue) {
      setNodes(definitionToRfNodes(initialDef, catalogue));
      setEdges(definitionToRfEdges(initialDef));
    }
  }, [initialDef, catalogue, setNodes, setEdges]);

  // ── View-mode switching ──────────────────────────────────────────────────────────────────────

  const switchToCode = useCallback(() => {
    // Serialize current canvas state into the code textarea
    const def = rfToDefinition(nodes, edges, initialDef ?? defaultFlowSkeleton());
    setCodeText(JSON.stringify(def, null, 2));
    setCodeError(null);
    setViewMode("code");
  }, [nodes, edges, initialDef]);

  const switchToDesign = useCallback(() => {
    // Parse and validate code text before switching
    try {
      const parsed = JSON.parse(codeText) as FlowDefinition;
      setNodes(definitionToRfNodes(parsed, catalogue));
      setEdges(definitionToRfEdges(parsed));
      setCodeError(null);
      setViewMode("design");
    } catch (e) {
      setCodeError(`Invalid JSON: ${(e as Error).message}`);
      // Stay in code view — do not switch
    }
  }, [codeText, catalogue, setNodes, setEdges]);

  const handleViewToggle = (next: ViewMode) => {
    if (next === viewMode) return;
    if (next === "code") switchToCode();
    else switchToDesign();
  };

  // ── Edge / drop callbacks ────────────────────────────────────────────────────────────────────

  const onConnect = useCallback(
    (params: { source: string | null; target: string | null; sourceHandle: string | null; targetHandle: string | null }) => {
      if (isBuiltIn) return;
      setEdges((eds) =>
        addEdge(
          {
            id: `e-${params.source}.${params.sourceHandle}-${params.target}.${params.targetHandle}`,
            source: params.source ?? "",
            target: params.target ?? "",
            sourceHandle: params.sourceHandle ?? undefined,
            targetHandle: params.targetHandle ?? undefined,
            type: "smoothstep",
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed, color: "#7CE3B6", width: 16, height: 16 },
            style: { stroke: "#7CE3B6", strokeWidth: 2, opacity: 0.85 }
          },
          eds
        )
      );
    },
    [setEdges, isBuiltIn]
  );

  const onDrop = useCallback((event: React.DragEvent) => {
    event.preventDefault();
    if (isBuiltIn) return;
    const type = event.dataTransfer.getData("application/reactflow");
    if (!type || !wrapper.current) return;
    const bounds = wrapper.current.getBoundingClientRect();
    const position = project({ x: event.clientX - bounds.left, y: event.clientY - bounds.top });
    const entry = catalogue?.[type];
    if (!entry) return;
    const id = `${type.split(".").pop()}-${Math.random().toString(36).slice(2, 7)}`;
    setNodes((nds) =>
      nds.concat({
        id,
        type: "fa-node",
        position,
        data: { typeId: type, params: defaultParams(entry), spec: entry, dynamicInputs: [] }
      })
    );
  }, [isBuiltIn, project, catalogue, setNodes]);

  const onDragOver = useCallback((event: React.DragEvent) => {
    event.preventDefault();
    event.dataTransfer.dropEffect = "move";
  }, []);

  // ── Save ─────────────────────────────────────────────────────────────────────────────────────

  const handleSave = async () => {
    if (isBuiltIn) return;
    setError(null);
    setCodeError(null);

    let defJson: string;

    if (viewMode === "code") {
      // Validate code view first
      try {
        JSON.parse(codeText); // validate
        defJson = codeText;
      } catch (e) {
        setCodeError(`Invalid JSON — save blocked: ${(e as Error).message}`);
        return;
      }
    } else {
      const def = rfToDefinition(nodes, edges, initialDef ?? defaultFlowSkeleton());
      defJson = JSON.stringify(def);
    }

    setIsSaving(true);
    try {
      await onSave(defJson);
    } catch (e: unknown) {
      const err = e as { data?: { error?: string }; message?: string };
      setError(err.data?.error ?? err.message ?? "Save failed");
    } finally {
      setIsSaving(false);
    }
  };

  // ── Node inspector ────────────────────────────────────────────────────────────────────────────

  const selectedNode = nodes.find((n) => n.id === selectedNodeId) ?? null;
  const updateSelectedParams = (nextParams: Record<string, unknown>) => {
    if (!selectedNode) return;
    setNodes((nds) => nds.map((n) => (n.id === selectedNode.id ? { ...n, data: { ...n.data, params: nextParams } } : n)));
  };

  // ── Node deletion ─────────────────────────────────────────────────────────────────────────────
  // Removes a node and every edge touching it. Used by the inspector button; the canvas also
  // supports Delete/Backspace via ReactFlow's deleteKeyCode (which routes through onNodesChange).
  const deleteNode = useCallback((id: string) => {
    if (isBuiltIn) return;
    setNodes((nds) => nds.filter((n) => n.id !== id));
    setEdges((eds) => eds.filter((e) => e.source !== id && e.target !== id));
    setSelectedNodeId((cur) => (cur === id ? null : cur));
  }, [isBuiltIn, setNodes, setEdges]);

  // ── Run node (model-only) ─────────────────────────────────────────────────────────────────────

  const onRunNode = async () => {
    if (!selectedNode || entityKind !== "model") return;
    setRunOutput(null);
    setError(null);
    try {
      const result = await runNode({
        nodeTypeId: selectedNode.data.typeId,
        params: selectedNode.data.params,
        inputs: {},
      }).unwrap();
      const lines = [];
      if (result.stdout) lines.push(result.stdout);
      if (result.error) lines.push(`ERROR: ${result.error}`);
      lines.push(`Outputs: ${JSON.stringify(result.outputs, null, 2)}`);
      lines.push(`Completed in ${result.durationMs}ms`);
      setRunOutput(lines.join("\n"));
    } catch (e: unknown) {
      const err = e as { data?: { error?: string } };
      setRunOutput(`Error: ${err.data?.error ?? "Run failed"}`);
    }
  };

  const nodeTypes = useMemo(() => ({ "fa-node": FlowNodeComponent }), []);

  // ── Render ────────────────────────────────────────────────────────────────────────────────────

  return (
    <div className="flex-1 flex min-h-0 overflow-hidden" data-fa-designer>
      {/* Left: palette + canvas/code */}
      <div className="flex-1 flex flex-col min-w-0">

        {/* Header strip */}
        <div className="shrink-0 px-4 sm:px-5 py-3 border-b border-fa-edge flex items-center gap-2 sm:gap-3 bg-fa-ink-2/40 backdrop-blur">
          <div className="min-w-0 flex-1">
            <h2 className="text-fa-frost-bright text-sm sm:text-base font-light truncate">{title}</h2>
            {isBuiltIn && (
              <div className="text-fa-frost-dim fa-caption hidden sm:block">
                read-only (built-in; duplicate to edit)
              </div>
            )}
          </div>

          {/* Design | Code toggle */}
          <div className="shrink-0 flex items-center rounded-md border border-fa-edge bg-fa-glass overflow-hidden">
            <button
              onClick={() => handleViewToggle("design")}
              className={cn(
                "inline-flex items-center gap-1 px-2.5 py-1 text-xs transition",
                viewMode === "design"
                  ? "bg-fa-frost-bright/20 text-fa-frost-bright"
                  : "text-fa-frost-dim hover:text-fa-frost-bright hover:bg-fa-glass-strong"
              )}
            >
              <Layout className="h-3 w-3" />
              Design
            </button>
            <div className="w-px h-4 bg-fa-edge" />
            <button
              onClick={() => handleViewToggle("code")}
              className={cn(
                "inline-flex items-center gap-1 px-2.5 py-1 text-xs transition",
                viewMode === "code"
                  ? "bg-fa-frost-bright/20 text-fa-frost-bright"
                  : "text-fa-frost-dim hover:text-fa-frost-bright hover:bg-fa-glass-strong"
              )}
            >
              <Code2 className="h-3 w-3" />
              Code
            </button>
          </div>

          <div className="flex items-center gap-2 shrink-0">
            {!isBuiltIn && (
              <button
                onClick={handleSave}
                disabled={isSaving}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-fa-frost-bright/20 hover:bg-fa-frost-bright/30 text-fa-frost-bright text-xs border border-fa-frost-bright/30 disabled:opacity-50 disabled:cursor-not-allowed transition"
              >
                {isSaving ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Save className="h-3.5 w-3.5" />}
                Save
              </button>
            )}
            <button
              onClick={onClose}
              data-fa-designer-close
              className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-dim hover:text-fa-frost-bright text-xs transition"
            >
              <X className="h-3.5 w-3.5" />
              Back
            </button>
          </div>
        </div>

        {/* Canvas / Code row */}
        <div className="flex-1 flex min-h-0">
          {/* Palette (design-only, hidden on mobile) */}
          {!isBuiltIn && viewMode === "design" && (
            <div className="hidden sm:block">
              <NodePalette catalogue={catalogue ?? {}} />
            </div>
          )}

          {viewMode === "design" ? (
            /* ── Design view ── */
            <div ref={wrapper} className="flex-1 relative" onDrop={onDrop} onDragOver={onDragOver}>
              <ReactFlow
                nodes={nodes}
                edges={edges}
                onNodesChange={onNodesChange}
                onEdgesChange={onEdgesChange}
                onConnect={onConnect}
                onNodeClick={(_e, n) => setSelectedNodeId(n.id)}
                onPaneClick={() => setSelectedNodeId(null)}
                onNodesDelete={(deleted) => {
                  if (deleted.some((n) => n.id === selectedNodeId)) setSelectedNodeId(null);
                }}
                deleteKeyCode={isBuiltIn ? null : ["Delete", "Backspace"]}
                nodeTypes={nodeTypes}
                fitView
                fitViewOptions={{ padding: 0.15, includeHiddenNodes: false, minZoom: 0.4, maxZoom: 1.2 }}
                minZoom={0.2}
                maxZoom={2}
                defaultEdgeOptions={{
                  type: "smoothstep",
                  animated: true,
                  markerEnd: { type: MarkerType.ArrowClosed, color: "#7CE3B6", width: 16, height: 16 },
                  style: { stroke: "#7CE3B6", strokeWidth: 2, opacity: 0.85 }
                }}
                proOptions={{ hideAttribution: true }}
                nodesDraggable={!isBuiltIn}
                nodesConnectable={!isBuiltIn}
                elementsSelectable
              >
                <Background gap={20} size={1} color="#1d2c43" />
                <Controls showInteractive={false} />
              </ReactFlow>
              {error && (
                <div className="absolute bottom-3 left-3 right-3 flex items-start gap-2 p-3 rounded-md border border-rose-300/30 bg-rose-300/5 text-rose-300 text-xs">
                  <AlertTriangle className="h-4 w-4 shrink-0 mt-0.5" />
                  <pre className="whitespace-pre-wrap font-mono">{error}</pre>
                </div>
              )}
            </div>
          ) : (
            /* ── Code view ── */
            <div className="flex-1 flex flex-col min-h-0 relative">
              <textarea
                className={cn(
                  "flex-1 w-full resize-none font-mono text-xs leading-relaxed p-4",
                  "bg-fa-ink text-fa-frost-bright border-0 outline-none focus:outline-none",
                  "placeholder:text-fa-frost-dim",
                  isBuiltIn && "opacity-60 cursor-default"
                )}
                value={codeText}
                readOnly={isBuiltIn}
                onChange={(e) => {
                  setCodeText(e.target.value);
                  setCodeError(null);
                }}
                spellCheck={false}
                autoComplete="off"
                autoCorrect="off"
                autoCapitalize="off"
                placeholder='{"schemaVersion":1,"nodes":[],"edges":[]}'
              />
              {(codeError || error) && (
                <div className="absolute bottom-3 left-3 right-3 flex items-start gap-2 p-3 rounded-md border border-rose-300/30 bg-rose-300/5 text-rose-300 text-xs">
                  <AlertTriangle className="h-4 w-4 shrink-0 mt-0.5" />
                  <pre className="whitespace-pre-wrap font-mono">{codeError ?? error}</pre>
                </div>
              )}
            </div>
          )}
        </div>
      </div>

      {/* Right: inspector + run panel (hidden on mobile) */}
      <div className="hidden sm:flex w-[280px] lg:w-[320px] shrink-0 border-l border-fa-edge bg-fa-ink-2/40 flex-col min-h-0">
        <div className="px-4 py-3 border-b border-fa-edge text-fa-frost-bright text-sm">Inspector</div>
        <div className="flex-1 overflow-y-auto">
          {viewMode === "code" ? (
            <div className="p-4 text-fa-frost-dim text-xs">
              Switch to <span className="text-fa-frost-bright">Design</span> view to inspect and edit individual nodes.
            </div>
          ) : selectedNode ? (
            <NodeInspector
              node={selectedNode}
              catalogue={catalogue ?? {}}
              onChange={updateSelectedParams}
              onDelete={() => deleteNode(selectedNode.id)}
              disabled={isBuiltIn}
            />
          ) : (
            <div className="p-4 text-fa-frost-dim text-xs">Select a node to edit its params.</div>
          )}
        </div>

        {/* Run & Inspect panel — model-only feature */}
        {entityKind === "model" && (
          <RunInspectPanel
            selectedNode={viewMode === "design" ? selectedNode : null}
            isRunning={isRunning}
            output={runOutput}
            onRun={onRunNode}
          />
        )}
      </div>
    </div>
  );
}

// ── Node palette ─────────────────────────────────────────────────────────────────────────────

function NodePalette({ catalogue }: { catalogue: Record<string, NodeCatalogueEntry> }) {
  const grouped = useMemo(() => {
    const out: Record<string, [string, NodeCatalogueEntry][]> = {};
    for (const [typeId, entry] of Object.entries(catalogue)) {
      (out[entry.category] ??= []).push([typeId, entry]);
    }
    for (const k of Object.keys(out)) out[k].sort((a, b) => a[0].localeCompare(b[0]));
    return out;
  }, [catalogue]);

  const order = ["data", "indicator", "feature", "compute", "model", "aggregator", "output"];

  return (
    <div className="w-[220px] shrink-0 border-r border-fa-edge bg-fa-ink-2/40 overflow-y-auto p-3 space-y-3">
      <div className="fa-overline text-fa-frost-dim">Palette</div>
      {order.filter((c) => grouped[c]).map((category) => (
        <div key={category}>
          <div className="fa-overline text-fa-frost-bright mb-1">{category}</div>
          <div className="space-y-1">
            {grouped[category].map(([typeId]) => (
              <div
                key={typeId}
                draggable
                onDragStart={(e) => {
                  e.dataTransfer.setData("application/reactflow", typeId);
                  e.dataTransfer.effectAllowed = "move";
                }}
                className="px-2 py-1.5 rounded-md border border-fa-edge bg-fa-glass text-fa-frost-bright fa-caption cursor-grab active:cursor-grabbing hover:border-fa-frost/30 hover:bg-fa-glass-strong transition"
                title="Drag onto the canvas"
              >
                {typeId}
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

// ── Custom node component ───────────────────────────────────────────────────────────────────

const CATEGORY_COLORS: Record<string, { card: string; header: string; accent: string; glow: string }> = {
  data:       { card: "border-sky-400/70 bg-[#0E2740]",       header: "bg-sky-500/40 text-sky-50",       accent: "#38BDF8", glow: "rgba(56,189,248,0.35)" },
  indicator:  { card: "border-violet-400/70 bg-[#231A40]",    header: "bg-violet-500/40 text-violet-50", accent: "#A78BFA", glow: "rgba(167,139,250,0.35)" },
  feature:    { card: "border-fuchsia-400/70 bg-[#2D1438]",   header: "bg-fuchsia-500/40 text-fuchsia-50", accent: "#E879F9", glow: "rgba(232,121,249,0.35)" },
  compute:    { card: "border-amber-400/70 bg-[#3A2A0E]",     header: "bg-amber-500/40 text-amber-50",   accent: "#FBBF24", glow: "rgba(251,191,36,0.35)" },
  model:      { card: "border-emerald-400/70 bg-[#0E3A2C]",   header: "bg-emerald-500/40 text-emerald-50", accent: "#34D399", glow: "rgba(52,211,153,0.35)" },
  aggregator: { card: "border-cyan-400/70 bg-[#0E353D]",      header: "bg-cyan-500/40 text-cyan-50",     accent: "#22D3EE", glow: "rgba(34,211,238,0.35)" },
  output:     { card: "border-rose-400/80 bg-[#3A1726]",      header: "bg-rose-500/50 text-rose-50",     accent: "#FB7185", glow: "rgba(251,113,133,0.4)" },
};

interface FaNodeData {
  typeId: string;
  params: Record<string, unknown>;
  spec: NodeCatalogueEntry;
  dynamicInputs: string[];
}

function FlowNodeComponent({ data, selected }: { data: FaNodeData; selected: boolean }) {
  const spec = data.spec;
  const palette = CATEGORY_COLORS[spec.category] ?? {
    card: "border-fa-edge bg-fa-glass", header: "bg-fa-glass-strong text-fa-frost-bright",
    accent: "#5C8AB4", glow: "rgba(92,138,180,0.25)"
  };
  const shadow = selected ? `0 0 0 2px ${palette.accent}, 0 12px 32px ${palette.glow}` : `0 8px 22px rgba(0,0,0,0.35)`;
  return (
    <div className={cn("rounded-lg border-2 text-fa-frost-bright transition-shadow", palette.card)}
         style={{ minWidth: 210, boxShadow: shadow }}>
      <div className={cn("px-3 py-1.5 rounded-t-md fa-overline font-semibold flex items-center gap-1", palette.header)}>
        {spec.category}
      </div>
      <div className="px-3 py-2 text-xs font-medium border-b border-white/10 text-white">{data.typeId}</div>
      <div className="py-2 grid grid-cols-2 gap-x-3">
        <div className="space-y-1">
          {spec.inputs.map((p) => (
            <div key={p.name} className="relative flex items-center fa-caption text-white/85 pl-2 pr-1 py-0.5">
              <Handle
                type="target"
                position={Position.Left}
                id={p.name}
                style={{ background: palette.accent, width: 10, height: 10, border: "2px solid #04080F", boxShadow: `0 0 6px ${palette.glow}` }}
              />
              <span className="ml-1 truncate" title={`${p.name} (${p.typeTag})`}>{p.name}</span>
            </div>
          ))}
          {data.dynamicInputs.map((name) => (
            <div key={`dyn-${name}`} className="relative flex items-center fa-caption text-white/70 pl-2 pr-1 py-0.5">
              <Handle
                type="target"
                position={Position.Left}
                id={name}
                style={{ background: "#04080F", width: 10, height: 10, border: `2px solid ${palette.accent}`, boxShadow: `0 0 6px ${palette.glow}` }}
              />
              <span className="ml-1 truncate italic" title={`${name} (dynamic column)`}>{name}</span>
            </div>
          ))}
        </div>
        <div className="space-y-1">
          {spec.outputs.map((p) => (
            <div key={p.name} className="relative flex items-center justify-end fa-caption text-white/85 pr-2 pl-1 py-0.5">
              <span className="mr-1 truncate" title={`${p.name} (${p.typeTag})`}>{p.name}</span>
              <Handle
                type="source"
                position={Position.Right}
                id={p.name}
                style={{ background: palette.accent, width: 10, height: 10, border: "2px solid #04080F", boxShadow: `0 0 6px ${palette.glow}` }}
              />
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

// ── Inspector (param editor) ────────────────────────────────────────────────────────────────

function NodeInspector({ node, catalogue, onChange, onDelete, disabled }:
    { node: RFNode<FaNodeData>; catalogue: Record<string, NodeCatalogueEntry>; onChange: (p: Record<string, unknown>) => void; onDelete: () => void; disabled: boolean }) {
  const typeId = node.data.typeId;
  const spec = catalogue[typeId];
  const params = node.data.params;

  // The delete affordance must be reachable even when the node's type isn't in the catalogue
  // (e.g. a stale/unknown node the user wants to remove), so render it before the unknown-type guard.
  const deleteButton = !disabled && (
    <button
      type="button"
      onClick={onDelete}
      title="Delete this node (or press Delete / Backspace)"
      className="w-full inline-flex items-center justify-center gap-1.5 px-3 py-1.5 rounded-md border border-rose-300/30 bg-rose-300/5 text-rose-300 hover:bg-rose-300/15 text-xs transition"
    >
      <Trash2 className="h-3.5 w-3.5" />
      Delete node
    </button>
  );

  if (!spec) return (
    <div className="p-4 space-y-3">
      <div className="text-fa-frost-dim text-xs">Unknown node type.</div>
      {deleteButton}
    </div>
  );

  return (
    <div className="p-4 space-y-3">
      <div>
        <div className="text-fa-frost-bright text-sm">{typeId}</div>
        <div className="fa-overline text-fa-frost-dim">{spec.category}</div>
      </div>
      {Object.entries(spec.params).length === 0 && (
        <div className="text-fa-frost-dim text-xs">No params for this node type.</div>
      )}
      {Object.entries(spec.params).map(([k, def]) => (
        <label key={k} className="block">
          <div className="fa-overline text-fa-frost-dim mb-1">
            {k} <span className="normal-case fa-caption text-fa-frost-dim/70">({def.typeTag})</span>
          </div>
          <input
            disabled={disabled}
            value={JSON.stringify(params[k] ?? def.default ?? "")}
            onChange={(e) => {
              try {
                const next = { ...params, [k]: JSON.parse(e.target.value) };
                onChange(next);
              } catch { /* leave unchanged until user finishes typing */ }
            }}
            className="w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1 text-fa-frost-bright text-xs font-mono disabled:opacity-50"
          />
          {def.description && <div className="text-fa-frost-dim/70 fa-caption mt-0.5">{def.description}</div>}
        </label>
      ))}
      {deleteButton && <div className="pt-1 border-t border-fa-edge">{deleteButton}</div>}
    </div>
  );
}

// ── Run & Inspect panel (model-only) ────────────────────────────────────────────────────────

function RunInspectPanel({
  selectedNode,
  isRunning,
  output,
  onRun,
}: {
  selectedNode: RFNode<FaNodeData> | null;
  isRunning: boolean;
  output: string | null;
  onRun: () => void;
}) {
  return (
    <div className="border-t border-fa-edge p-3 space-y-2 shrink-0">
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-1.5 text-fa-frost-bright text-xs">
          <Terminal className="h-3.5 w-3.5" /> Run &amp; Inspect
        </div>
        <button
          onClick={onRun}
          disabled={isRunning || !selectedNode}
          title={selectedNode ? `Run ${selectedNode.data.typeId}` : "Select a node to run it"}
          className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md bg-fa-frost-bright/15 hover:bg-fa-frost-bright/25 text-fa-frost-bright fa-caption border border-fa-frost-bright/30 disabled:opacity-40 disabled:cursor-not-allowed transition"
        >
          {isRunning ? <Loader2 className="h-3 w-3 animate-spin" /> : <Play className="h-3 w-3" />}
          {isRunning ? "Running…" : "Run node"}
        </button>
      </div>
      {isRunning && (
        <ProgressInline pct={null} label="Executing in sandbox…" tone="frost" />
      )}
      {!selectedNode && (
        <div className="text-fa-frost-dim fa-caption">Select a node on the canvas to run it through the sandbox.</div>
      )}
      {selectedNode && !output && !isRunning && (
        <div className="text-fa-frost-dim fa-caption">
          Node: <span className="text-fa-frost-bright font-mono">{selectedNode.data.typeId}</span>
          <br />Click "Run node" to execute through the sandbox sidecar.
        </div>
      )}
      {output && (
        <pre className="fa-caption font-mono text-fa-frost bg-fa-ink rounded-md p-2 overflow-auto max-h-28 whitespace-pre-wrap">
          {output}
        </pre>
      )}
    </div>
  );
}

// ── Flow ↔ ReactFlow translation ────────────────────────────────────────────────────────────

/**
 * Estimate rendered node height for auto-layout (same formula as before, kept here for
 * co-location with definitionToRfNodes which uses it directly).
 */
const NODE_HEADER_H = 26;
const NODE_LABEL_H  = 31;
const NODE_GRID_PAD = 16;
const NODE_PORT_ROW = 22;
const NODE_MIN_H    = 80;
const NODE_WIDTH    = 220;

function estimateNodeHeight(spec: NodeCatalogueEntry, dynamicInputCount: number): number {
  const portRows = Math.max(spec.inputs.length + dynamicInputCount, spec.outputs.length);
  const raw = NODE_HEADER_H + NODE_LABEL_H + NODE_GRID_PAD + portRows * NODE_PORT_ROW;
  return Math.max(NODE_MIN_H, raw);
}

/**
 * Height-aware left-to-right layered auto-layout.
 * Depth = longest path from any source node (topological BFS).
 * Within each layer, nodes are stacked top-to-bottom and centred vertically
 * relative to the tallest layer.
 */
function autoLayout(
  nodeIds: string[],
  edges: Array<{ from: string; to: string }>,
  nodeHeights: Record<string, number>,
  COL_GAP   = 300,
  ROW_GAP   = 40,
  PADDING_X = 60,
  PADDING_Y = 60,
): Record<string, { x: number; y: number }> {
  const successors = new Map<string, Set<string>>();
  const inDegree   = new Map<string, number>();
  for (const id of nodeIds) { successors.set(id, new Set()); inDegree.set(id, 0); }
  for (const e of edges) {
    const src = e.from.split(".")[0];
    const dst = e.to.split(".")[0];
    if (!nodeIds.includes(src) || !nodeIds.includes(dst)) continue;
    successors.get(src)?.add(dst);
    inDegree.set(dst, (inDegree.get(dst) ?? 0) + 1);
  }

  const depth   = new Map<string, number>(nodeIds.map((id) => [id, 0]));
  const queue   = nodeIds.filter((id) => (inDegree.get(id) ?? 0) === 0);
  const visited = new Set<string>();
  while (queue.length > 0) {
    const cur = queue.shift()!;
    if (visited.has(cur)) continue;
    visited.add(cur);
    const d = depth.get(cur) ?? 0;
    for (const next of successors.get(cur) ?? []) {
      if (d + 1 > (depth.get(next) ?? 0)) depth.set(next, d + 1);
      queue.push(next);
    }
  }
  const maxDepth = Math.max(0, ...depth.values());
  for (const id of nodeIds) {
    if (!visited.has(id)) depth.set(id, maxDepth + 1);
  }

  const layers = new Map<number, string[]>();
  for (const [id, d] of depth.entries()) {
    if (!layers.has(d)) layers.set(d, []);
    layers.get(d)!.push(id);
  }
  for (const arr of layers.values()) arr.sort();

  function layerTotalHeight(ids: string[]): number {
    const sum = ids.reduce((acc, id) => acc + (nodeHeights[id] ?? NODE_MIN_H), 0);
    return sum + Math.max(0, ids.length - 1) * ROW_GAP;
  }
  const maxLayerH = Math.max(0, ...[...layers.values()].map(layerTotalHeight));

  const positions: Record<string, { x: number; y: number }> = {};
  for (const [d, ids] of layers.entries()) {
    const lh = layerTotalHeight(ids);
    let y = PADDING_Y + (maxLayerH - lh) / 2;
    for (const id of ids) {
      positions[id] = { x: PADDING_X + d * COL_GAP, y };
      y += (nodeHeights[id] ?? NODE_MIN_H) + ROW_GAP;
    }
  }
  return positions;
}

function hasOverlapEstimated(
  nodes: Array<{ id: string; position: { x: number; y: number } }>,
  nodeHeights: Record<string, number>,
  nodeWidth = NODE_WIDTH,
): boolean {
  for (let i = 0; i < nodes.length; i++) {
    for (let j = i + 1; j < nodes.length; j++) {
      const a = nodes[i], b = nodes[j];
      const ah = nodeHeights[a.id] ?? NODE_MIN_H;
      const bh = nodeHeights[b.id] ?? NODE_MIN_H;
      const overlapX = a.position.x < b.position.x + nodeWidth  && b.position.x < a.position.x + nodeWidth;
      const overlapY = a.position.y < b.position.y + bh         && b.position.y < a.position.y + ah;
      if (overlapX && overlapY) return true;
    }
  }
  return false;
}

function definitionToRfNodes(def: FlowDefinition | null, catalogue: Record<string, NodeCatalogueEntry> | undefined): RFNode<FaNodeData>[] {
  if (!def || !catalogue) return [];

  const nodeSpecs = def.nodes.map((n) => {
    const spec = catalogue[n.type] ?? emptySpec();
    const fixed = new Set(spec.inputs.map((p) => p.name));
    const dynamicInputs = spec.acceptsAdditionalInputs
      ? [...new Set(
          def.edges
            .map((e) => e.to.split("."))
            .filter(([dst, port]) => dst === n.id && port && !fixed.has(port))
            .map(([, port]) => port)
        )]
      : [];
    return { n, spec, dynamicInputs };
  });

  const nodeHeights: Record<string, number> = {};
  for (const { n, spec, dynamicInputs } of nodeSpecs) {
    nodeHeights[n.id] = estimateNodeHeight(spec, dynamicInputs.length);
  }

  const hasMissingPos = def.nodes.some((n) => !n.position);
  const savedPositions = def.nodes.map((n) => ({ id: n.id, position: n.position ?? { x: 0, y: 0 } }));
  const needsLayout = hasMissingPos || hasOverlapEstimated(savedPositions, nodeHeights);

  let layoutPositions: Record<string, { x: number; y: number }> = {};
  if (needsLayout) {
    layoutPositions = autoLayout(
      def.nodes.map((n) => n.id),
      def.edges,
      nodeHeights,
    );
  }

  return nodeSpecs.map(({ n, spec, dynamicInputs }) => {
    const position = needsLayout
      ? (layoutPositions[n.id] ?? { x: 0, y: 0 })
      : (n.position ?? { x: 0, y: 0 });
    return {
      id: n.id,
      type: "fa-node",
      position,
      data: { typeId: n.type, params: n.params, spec, dynamicInputs }
    };
  });
}

function definitionToRfEdges(def: FlowDefinition | null): RFEdge[] {
  if (!def) return [];
  return def.edges.map((e, i) => {
    const [srcNode, srcPort] = e.from.split(".");
    const [dstNode, dstPort] = e.to.split(".");
    return {
      id: `e-${i}-${e.from}-${e.to}`,
      source: srcNode,
      target: dstNode,
      sourceHandle: srcPort,
      targetHandle: dstPort,
      type: "smoothstep",
      animated: true,
      markerEnd: { type: MarkerType.ArrowClosed, color: "#7CE3B6", width: 16, height: 16 },
      style: { stroke: "#7CE3B6", strokeWidth: 2, opacity: 0.85 }
    };
  });
}

function rfToDefinition(rfNodes: RFNode<FaNodeData>[], rfEdges: RFEdge[], base: FlowDefinition): FlowDefinition {
  const nodes: FlowNode[] = rfNodes.map((n) => ({
    id: n.id,
    type: n.data.typeId,
    params: n.data.params,
    position: { x: Math.round(n.position.x), y: Math.round(n.position.y) }
  }));
  const edges: FlowEdge[] = rfEdges.map((e) => ({
    from: `${e.source}.${e.sourceHandle ?? ""}`,
    to: `${e.target}.${e.targetHandle ?? ""}`
  }));
  return { ...base, nodes, edges };
}

function defaultFlowSkeleton(): FlowDefinition {
  return { schemaVersion: 1, modelKind: "deterministic", supportsBacktesting: true, warmupCandles: 60, nodes: [], edges: [] };
}

function emptySpec(): NodeCatalogueEntry {
  return { category: "unknown", inputs: [], outputs: [], params: {}, acceptsAdditionalInputs: false, requiresLiveData: false };
}

function defaultParams(entry: NodeCatalogueEntry): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [k, def] of Object.entries(entry.params)) {
    if (def.default !== undefined && def.default !== null) out[k] = def.default;
  }
  return out;
}
