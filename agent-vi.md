# Hướng dẫn Kho mã

## Cấu trúc Dự án & Tổ chức Mô-đun

`fakebookPayment/` chứa dịch vụ thanh toán ASP.NET Core chạy trên .NET 8. Mã nguồn được chia theo trách nhiệm: `GraphQL/` cung cấp các thao tác thanh toán, `Endpoints/` xử lý webhook PayOS nội bộ, `Services/` chứa luồng nghiệp vụ thanh toán và xác thực, `Repositories/` phụ trách truy cập cơ sở dữ liệu, còn `Workers/` thực hiện khởi tạo lược đồ và kích hoạt Premium. Các bản ghi miền nằm trong `Models/`, cấu hình liên kết nằm trong `Configuration/`, và các tiện ích xác thực yêu cầu nằm trong `Security/`. Lược đồ PostgreSQL nằm tại `fakebookPayment/schema.sql`.

`fakebookPayment.Tests/` chứa kiểm thử đơn vị, endpoint, lược đồ và kiểm thử tích hợp repository. Kiến trúc cùng hợp đồng liên dịch vụ được ghi trong `docs/handoffs/`; kế hoạch triển khai nằm trong `docs/superpowers/plans/`.

## Lệnh Build, Test & Phát triển

- `dotnet restore fakebookPayment.sln` khôi phục các gói NuGet.
- `dotnet build fakebookPayment.sln --no-restore` biên dịch dịch vụ và dự án kiểm thử.
- `dotnet test fakebookPayment.sln` chạy toàn bộ kiểm thử xUnit; Docker phải hoạt động để chạy các kiểm thử PostgreSQL Testcontainers.
- `dotnet run --project fakebookPayment/fakebookPayment.csproj` chạy API cục bộ trên cổng `5016`.
- `dotnet test fakebookPayment.Tests/fakebookPayment.Tests.csproj --filter FullyQualifiedName~PremiumPaymentServiceTests` chạy riêng một lớp kiểm thử.

## Quy tắc Viết mã & Đặt tên

Dùng thụt lề bốn dấu cách và quy ước C# tiêu chuẩn: `PascalCase` cho kiểu, phương thức và thành viên công khai; `camelCase` cho biến cục bộ và tham số; tiền tố `I` cho interface. Nullable reference types và implicit usings đã được bật. Giữ phần ánh xạ endpoint gọn, đặt quy tắc nghiệp vụ trong service và truy cập SQL trong repository. Ưu tiên API bất đồng bộ, truyền tiếp `CancellationToken`, và dùng dependency injection thay vì tự khởi tạo dependency hạ tầng. Chạy `dotnet format fakebookPayment.sln` trước khi gửi thay đổi định dạng diện rộng.

## Hướng dẫn Kiểm thử

Dự án dùng xUnit, `Microsoft.AspNetCore.Mvc.Testing` và Testcontainers. Đặt tên file và lớp theo đối tượng được kiểm thử, ví dụ `PaymentRepositoryIntegrationTests`, đồng thời đặt tên phương thức theo hành vi. Thêm unit test cho quy tắc miền và integration test khi thay đổi SQL, GraphQL, webhook hoặc cơ chế lưu trữ. Các integration test dùng chung PostgreSQL phải thuộc `PostgreSqlIntegrationCollection` và không chạy song song.

## Quy tắc Commit & Pull Request

Tuân theo phong cách conventional commit ngắn gọn của kho mã: `feat: implement ...`, `test: cover ...` hoặc `fix: handle ...`. Mỗi commit chỉ nên tập trung vào một thay đổi. Pull request cần giải thích thay đổi hành vi, liệt kê lệnh đã dùng để xác minh, liên kết issue hoặc hợp đồng liên quan, và nêu rõ thay đổi lược đồ hay cấu hình. Kèm ví dụ GraphQL request hoặc webhook payload khi hành vi API thay đổi; ảnh chụp màn hình thường không cần thiết cho backend này.

## Bảo mật & Cấu hình

Sao chép tên biến từ `.env.example`, nhưng không đưa secret thật của PayOS, gateway, authentication hoặc cơ sở dữ liệu vào Git, log, biến frontend hay file appsettings đã commit. Giữ `Payment__PaymentsEnabled=false` cho đến khi các dịch vụ phụ thuộc và định tuyến webhook được xác minh.
