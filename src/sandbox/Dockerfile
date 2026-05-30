FROM python:3.12-slim

# Create a non-root user for security.
RUN groupadd --gid 1001 sandbox && \
    useradd --uid 1001 --gid 1001 --no-create-home --shell /bin/false sandbox

WORKDIR /app

# Install dependencies as root, then switch user.
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy application source.
COPY app/ ./app/

# Own nothing writable by default; /tmp is handled via tmpfs mount in compose.
RUN chown -R sandbox:sandbox /app

USER sandbox

EXPOSE 8000

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
