# PayNex Professional Home Dashboard Update

This build updates the ERP home page to a professional SaaS ERP layout, excluding the large banner image.

Updated files:
- `src/PayNex.Cloud.Api/wwwroot/index.html`
- `src/PayNex.Cloud.Api/wwwroot/workspace.html`
- `src/PayNex.Cloud.Api/wwwroot/js/workspace.js`
- `src/PayNex.Cloud.Api/wwwroot/js/app-shell.js` through CSS-compatible shell styling
- `src/PayNex.Cloud.Api/wwwroot/css/app.css`

Main UI changes:
- Black professional top bar
- Narrow dark left icon navigation rail
- Company/workspace title area
- Calendar card on the left
- Work items assigned to me section
- Compact 5-column ERP module tile grid
- Circular blue/teal module icons
- No large banner image

Run:
```bash
cd src/PayNex.Cloud.Api
dotnet restore
dotnet run
```

Open:
- `/`
- `/workspace.html`
