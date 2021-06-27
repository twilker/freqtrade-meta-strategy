FROM freqtradeorg/freqtrade:stable

USER root

RUN apt-get update && \
    apt-get install -y \
        git
		
USER ftuser

RUN pip install ta

COPY ./.publish/* /freqtrade/

ENTRYPOINT ["/freqtrade/strategizer"]
