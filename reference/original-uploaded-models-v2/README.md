# Uploaded Model Files - V2

These are the latest model files uploaded by the user.

- `raw/` preserves the files exactly as uploaded.
- `normalized-by-class/` maps files by detected class content because some uploaded filenames may not match the class inside the file.
- The cloud API does not compile these raw WPF files directly, because some models use desktop-only WPF types such as `System.Windows.Media.ImageSource` and `Brush`.
- The cloud-safe converted implementation is in `src/PayNex.Cloud.Api/Models/PosDomainModels.cs`.

Included model areas:

- Cart line calculation
- Company information
- Customer master
- Dynamic fields
- Finance/accounting models
- Payment line
- Product master
- Report models
- Sales return models
- Sale result
- Sales invoice and customer ledger/payment models
- Shift info
- User account/session model
