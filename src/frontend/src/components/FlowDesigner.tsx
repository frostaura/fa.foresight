import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
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
import { AlertTriangle, Bot, Loader2, Save, Sparkles, X, Wand2 } from "lucide-react";
import { cn } from "../lib/cn";
import {
  useFlowAssistantMutation,
  useGetNodeCatalogueQuery,
  useUpdateModelMutation,
  type AssistantReply,
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
export default function FlowDesigner({ model, onClose }: { model: Model; onClose: () => void }) {
  // Portal to document.body so the `fixed inset-0` overlay escapes any transformed/filtered
  // ancestor (the card grid uses `transition` + `transform` hover effects that create a
  // containing block, which captures `position: fixed` to the card's bounds instead of the
  // viewport). Without the portal the designer renders trapped inside its parent card.
  return createPortal(
    <ReactFlowProvider>
      <DesignerInner model={model} onClose={onClose} />
    </ReactFlowProvider>,
    document.body
  );
}

function DesignerInner({ model, onClose }: { model: Model; onClose: () => void }) {
  const { data: catalogue } = useGetNodeCatalogueQuery();
  const [updateModel, { isLoading: isSaving }] = useUpdateModelMutation();
  const [assistant, { isLoading: isAsking, data: assistantReply }] = useFlowAssistantMutation();
  const [error, setError] = useState<string | null>(null);
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);
  const [pendingDiffDefinition, setPendingDiffDefinition] = useState<string | null>(null);

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

  const onApplyAssistant = (reply: AssistantReply) => {
    if (!reply.updatedDefinition) return;
    let parsed: FlowDefinition;
    try { parsed = JSON.parse(reply.updatedDefinition); } catch { return; }
    setNodes(definitionToRfNodes(parsed, catalogue));
    setEdges(definitionToRfEdges(parsed));
    setPendingDiffDefinition(null);
  };

  const nodeTypes = useMemo(() => ({ "fa-node": FlowNodeComponent }), []);

  return (
    <div
      className="fixed inset-0 z-[100] flex"
      style={{ background: "#040810" }}
      data-fa-designer
      onClick={(e) => e.stopPropagation()}
      onMouseDown={(e) => e.stopPropagation()}
    >
      <div className="flex-1 flex flex-col min-w-0">
        <div className="px-5 py-3 border-b border-fa-edge flex items-center gap-3 bg-fa-ink-2/40 backdrop-blur">
          <div className="min-w-0">
            <h2 className="text-fa-frost-bright text-base font-light truncate">{model.name}</h2>
            <div className="text-fa-frost-dim text-[11px]">
              {model.kind === "llm" ? "LLM model" : "Deterministic model"} · {model.supportsBacktesting ? "backtestable" : "live-only"}
              {isBuiltIn && " · read-only (built-in; duplicate to edit)"}
            </div>
          </div>
          <div className="ml-auto flex items-center gap-2">
            {!isBuiltIn && (
              <button onClick={onSave} disabled={isSaving}
                className="inline-flex items-center gap-2 px-3 py-1.5 rounded-md bg-fa-frost-bright/20 hover:bg-fa-frost-bright/30 text-fa-frost-bright text-sm border border-fa-frost-bright/30 disabled:opacity-50 disabled:cursor-not-allowed transition">
                {isSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                Save
              </button>
            )}
            <button onClick={onClose} data-fa-designer-close
              className="p-1.5 rounded-md text-fa-frost-dim hover:text-fa-frost-bright hover:bg-fa-glass transition">
              <X className="h-4 w-4" />
            </button>
          </div>
        </div>

        <div className="flex-1 flex min-h-0">
          {!isBuiltIn && <NodePalette catalogue={catalogue ?? {}} />}
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
            {pendingDiffDefinition && (
              <DiffPreviewOverlay
                onApply={() => onApplyAssistant({ updatedDefinition: pendingDiffDefinition } as AssistantReply)}
                onDiscard={() => setPendingDiffDefinition(null)}
              />
            )}
            {error && (
              <div className="absolute bottom-3 left-3 right-3 flex items-start gap-2 p-3 rounded-md border border-rose-300/30 bg-rose-300/5 text-rose-300 text-xs">
                <AlertTriangle className="h-4 w-4 shrink-0 mt-0.5" />
                <pre className="whitespace-pre-wrap font-mono">{error}</pre>
              </div>
            )}
          </div>
        </div>
      </div>

      <div className="w-[320px] shrink-0 border-l border-fa-edge bg-fa-ink-2/40 flex flex-col min-h-0">
        <div className="px-4 py-3 border-b border-fa-edge text-fa-frost-bright text-sm">Inspector</div>
        <div className="flex-1 overflow-y-auto">
          {selectedNode ? (
            <NodeInspector node={selectedNode} catalogue={catalogue ?? {}} onChange={updateSelectedParams} disabled={isBuiltIn} />
          ) : (
            <div className="p-4 text-fa-frost-dim text-xs">Select a node to edit its params.</div>
          )}
        </div>
        {!isBuiltIn && (
          <AiChatPanel
            modelId={model.id}
            isAsking={isAsking}
            reply={assistantReply ?? null}
            onSend={async (text) => {
              const reply = await assistant({
                id: model.id,
                intent: "modify",
                history: [{ role: "user", content: text }]
              }).unwrap();
              if (reply.updatedDefinition) setPendingDiffDefinition(reply.updatedDefinition);
            }}
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

// ── AI chat panel ────────────────────────────────────────────────────────────────────────────

function AiChatPanel({ modelId: _modelId, isAsking, reply, onSend }:
    { modelId: string; isAsking: boolean; reply: AssistantReply | null; onSend: (text: string) => Promise<void> }) {
  const [text, setText] = useState("");
  const submit = async () => {
    const t = text.trim();
    if (!t) return;
    setText("");
    try { await onSend(t); } catch { /* error shown via reply.error path */ }
  };
  return (
    <div className="border-t border-fa-edge p-3 space-y-2">
      <div className="flex items-center gap-1.5 text-fa-frost-bright text-xs">
        <Bot className="h-3.5 w-3.5" /> AI Assistant
      </div>
      {reply && (
        <div className="text-[11px] text-fa-frost-dim space-y-1">
          {reply.error ? (
            <div className="text-rose-300">{reply.error}</div>
          ) : reply.updatedDefinition ? (
            <div className="text-emerald-300">Diff ready — preview overlay shown on canvas.</div>
          ) : null}
          {reply.rationale && <div className="italic">{reply.rationale}</div>}
        </div>
      )}
      <textarea
        rows={3}
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder='e.g. "add a Bollinger feature and route bbU into the matrix"'
        className="w-full bg-fa-glass border border-fa-edge rounded-md p-2 text-fa-frost-bright text-[11px] resize-none"
      />
      <button onClick={submit} disabled={isAsking || !text.trim()}
        className="w-full inline-flex items-center justify-center gap-1.5 px-3 py-1.5 rounded-md bg-fa-frost-bright/20 hover:bg-fa-frost-bright/30 text-fa-frost-bright text-xs border border-fa-frost-bright/30 disabled:opacity-50 disabled:cursor-not-allowed transition">
        {isAsking ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wand2 className="h-3.5 w-3.5" />}
        {isAsking ? "Thinking…" : "Ask"}
      </button>
    </div>
  );
}

function DiffPreviewOverlay({ onApply, onDiscard }: { onApply: () => void; onDiscard: () => void }) {
  return (
    <div className="absolute top-3 left-1/2 -translate-x-1/2 flex items-center gap-3 px-4 py-2 rounded-md border border-emerald-300/40 bg-emerald-300/10 text-emerald-300 text-xs shadow-lg backdrop-blur">
      <Sparkles className="h-4 w-4" />
      Diff ready
      <button onClick={onApply} className="px-2 py-0.5 rounded-md bg-emerald-300/20 hover:bg-emerald-300/30">Apply</button>
      <button onClick={onDiscard} className="px-2 py-0.5 rounded-md bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-dim">Discard</button>
    </div>
  );
}

// ── Flow ↔ ReactFlow translation ────────────────────────────────────────────────────────────

function definitionToRfNodes(def: FlowDefinition | null, catalogue: Record<string, NodeCatalogueEntry> | undefined): RFNode<FaNodeData>[] {
  if (!def || !catalogue) return [];
  return def.nodes.map((n) => {
    const spec = catalogue[n.type] ?? emptySpec();
    // Collect the dynamic input ports actually wired into this node: target-port names of incoming
    // edges that aren't one of the spec's fixed inputs. Only meaningful for acceptsAdditionalInputs
    // nodes (matrix_builder), where each upstream feature column connects to its own column port.
    const fixed = new Set(spec.inputs.map((p) => p.name));
    const dynamicInputs = spec.acceptsAdditionalInputs
      ? [...new Set(
          def.edges
            .map((e) => e.to.split("."))
            .filter(([dst, port]) => dst === n.id && port && !fixed.has(port))
            .map(([, port]) => port)
        )]
      : [];
    return {
      id: n.id,
      type: "fa-node",
      position: n.position ?? { x: 0, y: 0 },
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
