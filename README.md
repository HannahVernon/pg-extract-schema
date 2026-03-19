# Project Title

## Overview
This project is designed to extract schema information from PostgreSQL databases in an efficient and comprehensive manner.

## Features
- Extract schema details, including tables, views, indexes, and constraints.
- Support for multiple output formats.
- Configurable extraction options.

## Requirements
- PostgreSQL 9.6 or later.
- Python 3.x.
- psycopg2 library for database connections.

## Installation
1. Clone the repository:
   ```bash
   git clone https://github.com/HannahVernon/pg-extract-schema.git
   cd pg-extract-schema
   ```
2. Install dependencies:
   ```bash
   pip install -r requirements.txt
   ```

## Usage
Run the script with appropriate parameters:
```bash
python extract_schema.py --db <database_name> --user <username> --password <password> --format <output_format>
```

## Examples
To extract schema information in JSON format:
```bash
python extract_schema.py --db mydatabase --user myusername --password mypassword --format json
```

## Output Format
The output can be generated in several formats:
- JSON
- CSV
- SQL

## Configuration
Configuration options can be set in the `config.json` file. Refer to the sample configuration file for details on available options.

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
- Initial release with basic extraction functionalities.
### [1.1.0] - 2026-03-19
- Added support for additional output formats and configuration options.