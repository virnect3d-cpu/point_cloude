#!/usr/bin/env node
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";
import { runJsx, toJsxPath, jsonWrap } from "../shared/comRunner.js";

const PROG_ID = "Illustrator.Application";

const TOOLS = [
  {
    name: "run_jsx",
    description: "Execute arbitrary ExtendScript inside Illustrator. Return the value of the last expression.",
    inputSchema: {
      type: "object",
      properties: { script: { type: "string" } },
      required: ["script"],
    },
  },
  {
    name: "open_file",
    description: "Open an .ai/.pdf/.svg file in Illustrator.",
    inputSchema: {
      type: "object",
      properties: { path: { type: "string" } },
      required: ["path"],
    },
  },
  {
    name: "save_as",
    description: "Save/export the active document. format: ai|svg|pdf|png",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string" },
        format: { type: "string", enum: ["ai", "svg", "pdf", "png"] },
      },
      required: ["path", "format"],
    },
  },
  {
    name: "get_document_info",
    description: "Return {name, width, height, artboardCount, layerCount} of the active document.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "list_layers",
    description: "Return layer names + counts in the active document as JSON.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "close_document",
    description: "Close the active document. saving: save|dontsave (default dontsave).",
    inputSchema: {
      type: "object",
      properties: { saving: { type: "string", enum: ["save", "dontsave"], default: "dontsave" } },
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
      if (args.format === "ai") {
        return `var o = new IllustratorSaveOptions(); app.activeDocument.saveAs(File(${JSON.stringify(p)}), o); "saved:"+${JSON.stringify(p)};`;
      }
      if (args.format === "svg") {
        return `var o = new ExportOptionsSVG(); o.embedRasterImages = false; app.activeDocument.exportFile(File(${JSON.stringify(p)}), ExportType.SVG, o); "saved:"+${JSON.stringify(p)};`;
      }
      if (args.format === "pdf") {
        return `var o = new PDFSaveOptions(); app.activeDocument.saveAs(File(${JSON.stringify(p)}), o); "saved:"+${JSON.stringify(p)};`;
      }
      // png
      return `var o = new ExportOptionsPNG24(); o.transparency = true; o.artBoardClipping = true; app.activeDocument.exportFile(File(${JSON.stringify(p)}), ExportType.PNG24, o); "saved:"+${JSON.stringify(p)};`;
    }

    case "get_document_info":
      return jsonWrap(`
        var d = app.activeDocument;
        return JSON.stringify({
          ok: true,
          name: d.name,
          width: d.width,
          height: d.height,
          artboardCount: d.artboards.length,
          layerCount: d.layers.length
        });
      `);

    case "list_layers":
      return jsonWrap(`
        var d = app.activeDocument;
        var out = [];
        for (var i=0; i<d.layers.length; i++) {
          var l = d.layers[i];
          out.push({ name: l.name, visible: l.visible, locked: l.locked, itemCount: l.pageItems.length });
        }
        return JSON.stringify({ ok: true, layers: out });
      `);

    case "close_document": {
      const s = args.saving ?? "dontsave";
      const opt = s === "save" ? "SaveOptions.SAVECHANGES" : "SaveOptions.DONOTSAVECHANGES";
      return `app.activeDocument.close(${opt}); "closed";`;
    }

    default:
      throw new Error(`Unknown tool: ${name}`);
  }
}

const server = new Server(
  { name: "illustrator-mcp", version: "0.1.0" },
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
      content: [{ type: "text", text: `illustrator-mcp error (${name}): ${err.message}` }],
    };
  }
});

const transport = new StdioServerTransport();
await server.connect(transport);
