FROM freqtradeorg/freqtrade:stable

USER root

RUN apt-get update && \
    apt-get install -y \
        git &&\
	pip install ta
		
USER ftuser

COPY ./.publish/* /freqtrade/

ENTRYPOINT ["/freqtrade/strategizer"]