# Disposable SSH host used ONLY by Backora's end-to-end tests (worker node and backup target roles).
FROM alpine:3.21
RUN apk add --no-cache openssh-server bash coreutils findutils util-linux tar gzip rclone socat procps age \
    && mkdir -p /run/sshd /root/.ssh /srv/data /BackoraDB \
    && chmod 700 /root/.ssh
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh
EXPOSE 22
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
