# pg-extract-schema by Hannah Vernon

## Overview
This project is designed to extract schema information from PostgreSQL databases in an efficient and comprehensive manner.

## Features
- Extract schema details, including tables, views, indexes, sequences, and constraints.

## Requirements
- PostgreSQL 9.6 or later.
- DotNet 9.0 Runtime or later.

## Installation
1. Clone the repository:
   ```bash
   git clone https://github.com/HannahVernon/pg-extract-schema.git
   cd pg-extract-schema
   ```
2. Install dependencies
   ```bash
   winget install Microsoft.DotNet.SDK.9
   ```
3. Build the binaries.
   ```bash
   dotnet build
   ```

## Usage
Run the executable with appropriate parameters:
```bash
Description:
  Extract DDL from a PostgreSQL database into discrete .sql files

Usage:
  pg-extract-schema [options]

Options:
  -h, --host <host> (REQUIRED)          PostgreSQL server hostname
  -p, --port <port>                     PostgreSQL server port [default: 5432]
  -d, --database <database> (REQUIRED)  Database name
  -s, --schema <schema>                 Schema name (default: all non-system schemas)
  -o, --output <output>                 Output directory [default: output]
  -U, --username <username>             PostgreSQL username [default: postgres]
  -W, --password <password>             PostgreSQL password (or set PGPASSWORD env var).  If password is not supplied on the command-line or via the environment variable, pg-extract-schema will ask for the password.
  --version                             Show version information
  -?, -h, --help                        Show help and usage information
```

## Examples

```bash
pg-extract-schema.exe -h mypgserver -p 5432 -d mydatabase -U me -o c:\temp\pg_schema
```

## License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Contributing
1. Fork the repository.
2. Create a new branch (`git checkout -b feature-foo`).
3. Make your changes and commit them (`git commit -m 'Add some feature'`).
4. Push to the branch (`git push origin feature-foo`).
5. Open a pull request.

## Support
For support, please open an issue in the GitHub repository. We will try to respond as quickly as possible.

## Changelog
### [1.0.0] - 2026-03-19
- Initial release
