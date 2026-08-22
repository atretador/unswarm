# Build stage
FROM golang:1.26-alpine AS builder
WORKDIR /app
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -o /agent ./cmd/agent

# Runtime stage
FROM gcr.io/distroless/static-debian12
COPY --from=builder /agent /agent
USER nonroot:nonroot
ENTRYPOINT ["/agent"]
