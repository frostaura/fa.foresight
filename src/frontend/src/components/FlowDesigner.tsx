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
import { AlertTriangle, Loader2, Play, Save, Terminal, X } from "lucide-react";
import { cn } from "../lib/cn";
import {
  useGetNodeCatalogueQuery,
  useRunFlowNodeMutation,
  useUpdateModelMutation,
  type FlowDefinition,
  type FlowEdge,
  type FlowNode,
  type Model,
  type NodeCatalogueEntry
} from "../store/api";

/**
 * Visual flow designer — the canvas users build prediction models in. Layered on reactflow:
 * one custom node component per category (data / indicator / feature / compute / model /
 * aggregator / output), source-driven from the server's NodeRegistry.GetCatalogueDescriptor()
 * so adding a new IFlowNode in the backend automatically surfaces in the palette without
 * frontend changes.
 *
 * The canvas is read-only for built-in models (the seeded "Foresight Default LLM" — users
 * duplicate it to edit). Editable models support drag-and-drop adds from the left palette,
 * edge drawing between port handles, per-node params editing in the right sidebar, and an
 * AI assistant chat at the bottom of the sidebar that emits structured diffs the user can
 * preview and apply.
 *
 * The flow definition is the source of truth — reactflow nodes/edges are derived from
 * (and synced back to) the parsed JSON. Save serializes back to the API.
 */
/**
 * FlowDesigner — renders inside the normal app layout (no portal, no fixed overlay).
 * The parent page is responsible for giving it a full-height flex container.
 */
export default function FlowDesigner({ model, onClose }: { model: Model; onClose: () => void }) {
  return (
    <ReactFlowProvider>
      <DesignerInner model={model} onClose={onClose} />
    </ReactFlowProvider>
  );
}

function DesignerInner({ model, onClose }: { model: Model; onClose: () => void }) {
  const { data: catalogue } = useGetNodeCatalogueQuery();
  const [updateModel, { isLoading: isSaving }] = useUpdateModelMutation();
  const [runNode, { isLoading: isRunning }] = useRunFlowNodeMutation();
  const [error, setError] = useState<string | null>(null);
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);
  const [runOutput, setRunOutput] = useState<string | null>(null);

  // Parse the current flow definition once on mount + when it changes (e.g. after assistant apply).
  const initialDef = useMemo<FlowDefinition | null>(() => {
    try { return JSON.parse(model.definition); } catch { return null; }
  }, [model.definition]);

  const [nodes, setNodes, onNodesChange] = useNodesState(definitionToRfNodes(initialDef, catalogue));
  const [edges, setEdges, onEdgesChange] = useEdgesState(definitionToRfEdges(initialDef));
  const { project } = useReactFlow();
  const wrapper = useRef<HTMLDivElement>(null);
  const isBuiltIn = model.isBuiltIn;

  useEffect(() => {
    if (initialDef && catalogue) {
      setNodes(definitionToRfNodes(initialDef, catalogue));
      setEdges(definitionToRfEdges(initialDef));
    }
  }, [initialDef, catalogue, setNodes, setEdges]);

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

  const onSave = async () => {
    if (isBuiltIn) return;
    setError(null);
    const def = rfToDefinition(nodes, edges, initialDef ?? defaultFlowSkeleton());
    try {
      await updateModel({ id: model.id, body: { definition: JSON.stringify(def) } }).unwrap();
      onClose();
    } catch (e: unknown) {
      const err = e as { data?: { error?: string } };
      setError(err.data?.error ?? "Save failed");
    }
  };

  const selectedNode = nodes.find((n) => n.id === selectedNodeId) ?? null;
  const updateSelectedParams = (nextParams: Record<string, unknown>) => {
    if (!selectedNode) return;
    setNodes((nds) => nds.map((n) => (n.id === selectedNode.id ? { ...n, data: { ...n.data, params: nextParams } } : n)));
  };

  // Run the currently-selected node through the sandbox sidecar.
  const onRunNode = async () => {
    if (!selectedNode) return;
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

  return (
    <div
      className="flex-1 flex min-h-0 overflow-hidden"
      data-fa-designer
    >
      {/* Left: palette + canvas */}
      <div className="flex-1 flex flex-col min-w-0">
        {/* Header strip — model name, Save, Back */}
        <div className="shrink-0 px-4 sm:px-5 py-3 border-b border-fa-edge flex items-center gap-2 sm:gap-3 bg-fa-ink-2/40 backdrop-blur">
          <div className="min-w-0 flex-1">
            <h2 className="text-fa-frost-bright text-sm sm:text-base font-light truncate">{model.name}</h2>
            <div className="text-fa-frost-dim text-[10px] sm:text-[11px] hidden sm:block">
              {model.kind === "llm" ? "LLM model" : "Deterministic model"} · {model.supportsBacktesting ? "backtestable" : "live-only"}
              {isBuiltIn && " · read-only (built-in; duplicate to edit)"}
            </div>
          </div>
          <div className="flex items-center gap-2 shrink-0">
            {!isBuiltIn && (
              <button onClick={onSave} disabled={isSaving}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-fa-frost-bright/20 hover:bg-fa-frost-bright/30 text-fa-frost-bright text-xs border border-fa-frost-bright/30 disabled:opacity-50 disabled:cursor-not-allowed transition">
                {isSaving ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Save className="h-3.5 w-3.5" />}
                Save
              </button>
            )}
            <button onClick={onClose} data-fa-designer-close
              className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-dim hover:text-fa-frost-bright text-xs transition">
              <X className="h-3.5 w-3.5" />
              Back
            </button>
          </div>
        </div>

        {/* Canvas row: palette (hidden on mobile) + flow */}
        <div className="flex-1 flex min-h-0">
          {/* Palette: hidden at < sm so the canvas gets all horizontal space on mobile. */}
          {!isBuiltIn && (
            <div className="hidden sm:block">
              <NodePalette catalogue={catalogue ?? {}} />
            </div>
          )}
          <div ref={wrapper} className="flex-1 relative" onDrop={onDrop} onDragOver={onDragOver}>
            <ReactFlow
              nodes={nodes}
              edges={edges}
              onNodesChange={onNodesChange}
              onEdgesChange={onEdgesChange}
              onConnect={onConnect}
              onNodeClick={(_e, n) => setSelectedNodeId(n.id)}
              onPaneClick={() => setSelectedNodeId(null)}
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
        </div>
      </div>

      {/* Right: inspector + run panel — hidden on mobile to avoid horizontal overflow on
          the canvas. At sm+ it slides back in at its usual width. */}
      <div className="hidden sm:flex w-[280px] lg:w-[320px] shrink-0 border-l border-fa-edge bg-fa-ink-2/40 flex-col min-h-0">
        <div className="px-4 py-3 border-b border-fa-edge text-fa-frost-bright text-sm">Inspector</div>
        <div className="flex-1 overflow-y-auto">
          {selectedNode ? (
            <NodeInspector node={selectedNode} catalogue={catalogue ?? {}} onChange={updateSelectedParams} disabled={isBuiltIn} />
          ) : (
            <div className="p-4 text-fa-frost-dim text-xs">Select a node to edit its params.</div>
          )}
        </div>
        {/* Run & Inspect panel — replaces the AI assistant chat. Runs the selected node
            through the sandbox sidecar (POST /api/flows/run-node) and shows stdout + outputs. */}
        <RunInspectPanel
          selectedNode={selectedNode}
          isRunning={isRunning}
          output={runOutput}
          onRun={onRunNode}
        />
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
      <div className="text-fa-frost-dim text-[10px] uppercase tracking-wider">Palette</div>
      {order.filter((c) => grouped[c]).map((category) => (
        <div key={category}>
          <div className="text-fa-frost-bright text-[11px] uppercase tracking-wider mb-1">{category}</div>
          <div className="space-y-1">
            {grouped[category].map(([typeId]) => (
              <div
                key={typeId}
                draggable
                onDragStart={(e) => {
                  e.dataTransfer.setData("application/reactflow", typeId);
                  e.dataTransfer.effectAllowed = "move";
                }}
                className="px-2 py-1.5 rounded-md border border-fa-edge bg-fa-glass text-fa-frost-bright text-[11px] cursor-grab active:cursor-grabbing hover:border-fa-frost/30 hover:bg-fa-glass-strong transition"
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

// Per-category palette. Card edge is bright (60% opacity) and the fill is a deeper translucent
// wash (15%) over the dark canvas so the node body reads clearly against the grid background.
// Category headers also tint to the same hue.
// Per-category palette. Card body is opaque (~85%) so node interiors read clearly against the
// dark grid; borders use the full hue at 70% for a vivid edge. Header is a deeper saturated wash
// of the same hue so the eye groups nodes by category instantly.
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
  // Dynamic per-column input ports for nodes with acceptsAdditionalInputs (e.g. feature.matrix_builder):
  // the target-port names of incoming edges that aren't fixed inputs in the spec. Rendered as extra
  // handles so each connected feature column anchors to a visible dot instead of dangling — otherwise
  // the upstream feature packs look like their outputs go nowhere.
  dynamicInputs: string[];
}

function FlowNodeComponent({ data, selected }: { data: FaNodeData; selected: boolean }) {
  const spec = data.spec;
  const palette = CATEGORY_COLORS[spec.category] ?? {
    card: "border-fa-edge bg-fa-glass", header: "bg-fa-glass-strong text-fa-frost-bright",
    accent: "#5C8AB4", glow: "rgba(92,138,180,0.25)"
  };
  // When selected, lift the card with a colored glow ring so the inspector picks up the focused node
  // immediately. Default state still has a soft ambient shadow so the cards have weight on the canvas.
  const shadow = selected ? `0 0 0 2px ${palette.accent}, 0 12px 32px ${palette.glow}` : `0 8px 22px rgba(0,0,0,0.35)`;
  return (
    <div className={cn("rounded-lg border-2 text-fa-frost-bright transition-shadow", palette.card)}
         style={{ minWidth: 210, boxShadow: shadow }}>
      <div className={cn("px-3 py-1.5 rounded-t-md text-[10px] uppercase tracking-wider font-semibold flex items-center gap-1", palette.header)}>
        {spec.category}
      </div>
      <div className="px-3 py-2 text-xs font-medium border-b border-white/10 text-white">{data.typeId}</div>
      <div className="py-2 grid grid-cols-2 gap-x-3">
        <div className="space-y-1">
          {spec.inputs.map((p) => (
            <div key={p.name} className="relative flex items-center text-[10px] text-white/85 pl-2 pr-1 py-0.5">
              <Handle
                type="target"
                position={Position.Left}
                id={p.name}
                style={{ background: palette.accent, width: 10, height: 10, border: "2px solid #04080F", boxShadow: `0 0 6px ${palette.glow}` }}
              />
              <span className="ml-1 truncate" title={`${p.name} (${p.typeTag})`}>{p.name}</span>
            </div>
          ))}
          {/* Dynamic per-column inputs (matrix_builder): one handle per wired feature column so the
              upstream packs visibly land on a port instead of dangling. Hollow dot distinguishes them
              from the spec's fixed inputs. */}
          {data.dynamicInputs.map((name) => (
            <div key={`dyn-${name}`} className="relative flex items-center text-[10px] text-white/70 pl-2 pr-1 py-0.5">
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
            <div key={p.name} className="relative flex items-center justify-end text-[10px] text-white/85 pr-2 pl-1 py-0.5">
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

function NodeInspector({ node, catalogue, onChange, disabled }:
    { node: RFNode<FaNodeData>; catalogue: Record<string, NodeCatalogueEntry>; onChange: (p: Record<string, unknown>) => void; disabled: boolean }) {
  const typeId = node.data.typeId;
  const spec = catalogue[typeId];
  const params = node.data.params;
  if (!spec) return <div className="p-4 text-fa-frost-dim text-xs">Unknown node type.</div>;

  return (
    <div className="p-4 space-y-3">
      <div>
        <div className="text-fa-frost-bright text-sm">{typeId}</div>
        <div className="text-fa-frost-dim text-[10px] uppercase tracking-wider">{spec.category}</div>
      </div>
      {Object.entries(spec.params).length === 0 && (
        <div className="text-fa-frost-dim text-xs">No params for this node type.</div>
      )}
      {Object.entries(spec.params).map(([k, def]) => (
        <label key={k} className="block">
          <div className="text-fa-frost-dim text-[10px] uppercase tracking-wider mb-1">
            {k} <span className="normal-case text-[10px] text-fa-frost-dim/70">({def.typeTag})</span>
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
          {def.description && <div className="text-fa-frost-dim/70 text-[10px] mt-0.5">{def.description}</div>}
        </label>
      ))}
    </div>
  );
}

// ── Run & Inspect panel (replaces AI chat) ───────────────────────────────────────────────────
//
// Runs the currently-selected node through the sandbox sidecar (POST /api/flows/run-node)
// and surfaces stdout + outputs. The sandbox is network-isolated and purity-enforced so the
// result is deterministic — same definition + inputs = identical output.

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
          className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md bg-fa-frost-bright/15 hover:bg-fa-frost-bright/25 text-fa-frost-bright text-[11px] border border-fa-frost-bright/30 disabled:opacity-40 disabled:cursor-not-allowed transition"
        >
          {isRunning ? <Loader2 className="h-3 w-3 animate-spin" /> : <Play className="h-3 w-3" />}
          {isRunning ? "Running…" : "Run node"}
        </button>
      </div>
      {!selectedNode && (
        <div className="text-fa-frost-dim text-[11px]">Select a node on the canvas to run it through the sandbox.</div>
      )}
      {selectedNode && !output && !isRunning && (
        <div className="text-fa-frost-dim text-[11px]">
          Node: <span className="text-fa-frost-bright font-mono">{selectedNode.data.typeId}</span>
          <br />Click "Run node" to execute through the sandbox sidecar.
        </div>
      )}
      {output && (
        <pre className="text-[10px] font-mono text-fa-frost bg-fa-ink rounded-md p-2 overflow-auto max-h-28 whitespace-pre-wrap leading-relaxed">
          {output}
        </pre>
      )}
    </div>
  );
}

// ── Flow ↔ ReactFlow translation ────────────────────────────────────────────────────────────

/**
 * Estimate the rendered height (in pixels) of a single FlowNodeComponent.
 *
 * The custom node renders as:
 *   ┌─────────────────────────┐  ← category header   py-1.5 ≈ 26px
 *   │ type label              │  ← label row          py-2   ≈ 30px  (+ 1px border-b)
 *   ├─────────────────────────┤
 *   │  input col │ output col │  ← port grid          py-2 wrapper (16px) + rows
 *   └─────────────────────────┘
 *
 * Each port row is a flex item with py-0.5 (4px top+bottom) and a 10px handle + text-[10px]:
 * effective row height ≈ 22px.
 *
 * The grid has two columns so the number of rows = max(inputs, outputs).
 * For matrix_builder with 13 dynamic inputs + fixed inputs the estimate is generous enough
 * that the node's column consumes the vertical room it truly needs.
 *
 * Constants (all in px):
 *   HEADER_H  = 26   – category badge (py-1.5 × 2 + ~10px text line)
 *   LABEL_H   = 31   – type label row (py-2 × 2 + ~11px text + 1px border)
 *   GRID_PAD  = 16   – py-2 on the port grid wrapper (8px top + 8px bottom)
 *   PORT_ROW  = 22   – each port row (py-0.5 × 2 + ~10px text/handle)
 *   MIN_H     = 80   – floor so zero-port nodes still have a visible box
 */
const NODE_HEADER_H = 26;
const NODE_LABEL_H  = 31;
const NODE_GRID_PAD = 16;
const NODE_PORT_ROW = 22;
const NODE_MIN_H    = 80;
const NODE_WIDTH    = 220; // generous estimate; minWidth in CSS is 210

function estimateNodeHeight(spec: NodeCatalogueEntry, dynamicInputCount: number): number {
  const portRows = Math.max(spec.inputs.length + dynamicInputCount, spec.outputs.length);
  const raw = NODE_HEADER_H + NODE_LABEL_H + NODE_GRID_PAD + portRows * NODE_PORT_ROW;
  return Math.max(NODE_MIN_H, raw);
}

/**
 * Height-aware left→right layered auto-layout.
 *
 * Returns a map of node id → {x, y} position.
 *
 * Algorithm:
 *   1. Assign each node a depth = longest path from any source (node with no in-edges).
 *      Sources get depth 0. BFS propagates max depth to successors.
 *   2. Group nodes by depth into columns (layers).
 *   3. x = depth * COL_GAP + PADDING_X   (COL_GAP = NODE_WIDTH + horizontal breathing room)
 *   4. Within each layer, stack nodes top→down using each node's ESTIMATED height + a gap:
 *        y[i] = PADDING_Y + sum(heights[0..i-1]) + i * ROW_GAP
 *      The tallest layer sets the total canvas height; shorter layers are centred vertically.
 *
 * This means a tall node (e.g. matrix_builder with 13 ports, ~358px estimated height) pushes
 * the next node in its column down by 358px + ROW_GAP, while a compact node with 2 ports
 * (~116px) only needs ~156px of vertical space — so no two nodes in the same column ever
 * collide, regardless of port count.
 *
 * v6 walk-through (Foresight v6 flow, 9 nodes):
 *   depth 0: source.binance.klines           (~116px) → col x=60
 *   depth 1: 5 × indicator.*                 (~116px each) → col x=360, stacked with 40px gap
 *              total column height ≈ 5×116 + 4×40 = 740px
 *   depth 2: feature.matrix_builder          (~358px) → col x=660, centred at 370px
 *   depth 3: model.logistic_regression       (~116px) → col x=960
 *   depth 4: output.prediction               (~116px) → col x=1260
 *
 *   The indicator column (depth 1) is the tallest at ~740px; matrix_builder (one node, 358px)
 *   is centred within that 740px band → its box occupies [191, 549], well clear of any other node.
 */
function autoLayout(
  nodeIds: string[],
  edges: Array<{ from: string; to: string }>,
  nodeHeights: Record<string, number>,
  COL_GAP    = 300,   // horizontal distance between column left edges (NODE_WIDTH + 80px breathing)
  ROW_GAP    = 40,    // vertical gap inserted between every pair of adjacent nodes in a column
  PADDING_X  = 60,
  PADDING_Y  = 60,
): Record<string, { x: number; y: number }> {
  // Build adjacency: srcId → Set<dstId>, and in-degree map
  const successors = new Map<string, Set<string>>();
  const inDegree = new Map<string, number>();
  for (const id of nodeIds) { successors.set(id, new Set()); inDegree.set(id, 0); }
  for (const e of edges) {
    const src = e.from.split(".")[0];
    const dst = e.to.split(".")[0];
    if (!nodeIds.includes(src) || !nodeIds.includes(dst)) continue;
    successors.get(src)?.add(dst);
    inDegree.set(dst, (inDegree.get(dst) ?? 0) + 1);
  }
  // Longest-path depth via relaxation (topological BFS from sources)
  const depth = new Map<string, number>(nodeIds.map((id) => [id, 0]));
  const queue: string[] = nodeIds.filter((id) => (inDegree.get(id) ?? 0) === 0);
  const visited = new Set<string>();
  while (queue.length > 0) {
    const cur = queue.shift()!;
    if (visited.has(cur)) continue;
    visited.add(cur);
    const d = depth.get(cur) ?? 0;
    for (const next of successors.get(cur) ?? []) {
      const nd = depth.get(next) ?? 0;
      if (d + 1 > nd) depth.set(next, d + 1);
      queue.push(next);
    }
  }
  // Nodes not reached (cycles / islands) get depth = max+1 so they still appear
  const maxDepth = Math.max(0, ...depth.values());
  for (const id of nodeIds) {
    if (!visited.has(id)) depth.set(id, maxDepth + 1);
  }
  // Group by depth → layers; sort for determinism
  const layers = new Map<number, string[]>();
  for (const [id, d] of depth.entries()) {
    if (!layers.has(d)) layers.set(d, []);
    layers.get(d)!.push(id);
  }
  for (const arr of layers.values()) arr.sort();

  // Compute each layer's total height (sum of node heights + gaps between them)
  function layerTotalHeight(ids: string[]): number {
    const sum = ids.reduce((acc, id) => acc + (nodeHeights[id] ?? NODE_MIN_H), 0);
    return sum + Math.max(0, ids.length - 1) * ROW_GAP;
  }
  const maxLayerH = Math.max(0, ...[...layers.values()].map(layerTotalHeight));

  const positions: Record<string, { x: number; y: number }> = {};
  for (const [d, ids] of layers.entries()) {
    const lh = layerTotalHeight(ids);
    // Centre this layer vertically within the tallest layer's band
    let y = PADDING_Y + (maxLayerH - lh) / 2;
    ids.forEach((id) => {
      positions[id] = { x: PADDING_X + d * COL_GAP, y };
      y += (nodeHeights[id] ?? NODE_MIN_H) + ROW_GAP;
    });
  }
  return positions;
}

/**
 * Check whether any two nodes overlap given per-node estimated bounding boxes.
 * Uses the same height estimates as autoLayout so the overlap check is consistent
 * with the layout that will be applied when overlap is detected.
 */
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

  // Pre-compute per-node specs and dynamic inputs so we can estimate heights before layout.
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

  // Build per-node height estimates (used for both overlap-check and layout).
  const nodeHeights: Record<string, number> = {};
  for (const { n, spec, dynamicInputs } of nodeSpecs) {
    nodeHeights[n.id] = estimateNodeHeight(spec, dynamicInputs.length);
  }

  // Decide whether auto-layout is needed:
  //   • Any node is missing a position, OR
  //   • Any two nodes overlap given their estimated bounding boxes.
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
      type: "smoothstep",                              // gentle curves instead of bezier hairpins
      animated: true,                                   // dashed flow animation
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
