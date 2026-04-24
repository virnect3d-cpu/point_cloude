#!/usr/bin/env node
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";
import { runJsx, toJsxPath, jsonWrap } from "../shared/comRunner.js";

const PROG_ID = "Photoshop.Application";

const TOOLS = [
  {
    name: "run_jsx",
    description: "Execute arbitrary ExtendScript (JavaScript) inside Photoshop. Return the value of the last expression.",
    inputSchema: {
      type: "object",
      properties: { script: { type: "string", description: "ExtendScript source" } },
      required: ["script"],
    },
  },
  {
    name: "open_file",
    description: "Open a file (PSD/PNG/JPG/etc.) in Photoshop. Absolute path required.",
    inputSchema: {
      type: "object",
      properties: { path: { type: "string" } },
      required: ["path"],
    },
  },
  {
    name: "save_as",
    description: "Save the active document. format: psd|png|jpg. quality (jpg only) 0-12.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string" },
        format: { type: "string", enum: ["psd", "png", "jpg"] },
        quality: { type: "number", minimum: 0, maximum: 12, default: 10 },
      },
      required: ["path", "format"],
    },
  },
  {
    name: "get_document_info",
    description: "Return {name, width, height, resolution, mode, layerCount} of the active document as JSON.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "list_layers",
    description: "Return a flat list of layer names + kinds in the active document as JSON.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "close_document",
    description: "Close the active document. saving: save|dontsave|prompt (default dontsave).",
    inputSchema: {
      type: "object",
      properties: { saving: { type: "string", enum: ["save", "dontsave", "prompt"], default: "dontsave" } },
    },
  },
];

function buildScript(name, args) {
  switch (name) {
    case "run_jsx":
      return args.script;

    case "open_file":
      return `app.open(File(${JSON.stringify(toJsxPath(args.path))})); app.activeDocument.name;`;

    case "save_as": {
      const p = toJsxPath(args.path);
      const q = args.quality ?? 10;
      if (args.format === "psd") {
        return `var o = new PhotoshopSaveOptions(); o.embedColorProfile = true; o.alphaChannels = true; o.layers = true; app.activeDocument.saveAs(File(${JSON.stringify(p)}), o, true); "saved:"+${JSON.stringify(p)};`;
      }
      if (args.format === "png") {
        return `var o = new PNGSaveOptions(); o.interlaced = false; app.activeDocument.saveAs(File(${JSON.stringify(p)}), o, true, Extension.LOWERCASE); "saved:"+${JSON.stringify(p)};`;
      }
      // jpg
      return `var o = new JPEGSaveOptions(); o.quality = ${q}; app.activeDocument.saveAs(File(${JSON.stringify(p)}), o, true, Extension.LOWERCASE); "saved:"+${JSON.stringify(p)};`;
    }

    case "get_document_info":
      return jsonWrap(`
        var d = app.activeDocument;
        return JSON.stringify({
          ok: true,
          name: d.name,
          width: d.width.value,
          height: d.height.value,
          resolution: d.resolution,
          mode: String(d.mode),
          layerCount: d.layers.length
        });
      `);

    case "list_layers":
      return jsonWrap(`
        var d = app.activeDocument;
        var out = [];
        function walk(layers, depth) {
          for (var i=0; i<layers.length; i++) {
            var l = layers[i];
            out.push({ name: l.name, kind: String(l.typename), depth: depth, visible: l.visible });
            if (l.typename === 'LayerSet' && l.layers) walk(l.layers, depth+1);
          }
        }
        walk(d.layers, 0);
        return JSON.stringify({ ok: true, layers: out });
      `);

    case "close_document": {
      const s = args.saving ?? "dontsave";
      const map = { save: "SaveOptions.SAVECHANGES", dontsave: "SaveOptions.DONOTSAVECHANGES", prompt: "SaveOptions.PROMPTTOSAVECHANGES" };
      return `app.activeDocument.close(${map[s]}); "closed";`;
    }

    default:
      throw new Error(`Unknown tool: ${name}`);
  }
}

const server = new Server(
  { name: "photoshop-mcp", version: "0.1.0" },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: TOOLS }));

server.setRequestHandler(CallToolRequestSchema, async (req) => {
  const { name, arguments: args = {} } = req.params;
  try {
    const script = buildScript(name, args);
    const output = await runJsx(PROG_ID, script);
    return { content: [{ type: "text", text: output || "(no output)" }] };
  } catch (err) {
    return {
      isError: true,
      content: [{ type: "text", text: `photoshop-mcp error (${name}): ${err.message}` }],
    };
  }
});

const transport = new StdioServerTransport();
await server.connect(transport);
